using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.Bases;
using WalletWasabi.WabiSabi.Backend.PostRequests;
using WalletWasabi.WabiSabi.Models;

namespace WalletWasabi.WabiSabi.Client
{
	public class RoundStateUpdater : PeriodicRunner
	{
		// Denotes that each round should be checked against a predicate list.
		private static readonly uint256 AnyRoundId = uint256.Zero;

		public RoundStateUpdater(TimeSpan requestInterval, IWabiSabiApiRequestHandler arenaRequestHandler) : base(requestInterval)
		{
			ArenaRequestHandler = arenaRequestHandler;
		}

		private IWabiSabiApiRequestHandler ArenaRequestHandler { get; }
		private Dictionary<uint256, RoundState> RoundStates { get; set; } = new();

		private Dictionary<uint256, List<RoundStateAwaiter>> Awaiters { get; } = new();
		private object AwaitersLock { get; } = new();

		protected override async Task ActionAsync(CancellationToken cancellationToken)
		{
			var statusResponse = await ArenaRequestHandler.GetStatusAsync(cancellationToken).ConfigureAwait(false);
			var responseRoundStates = statusResponse.ToDictionary(round => round.Id);

			var updatedRoundStates = responseRoundStates.Where(round => RoundStates.ContainsKey(round.Key));
			var newRoundStates = responseRoundStates.Where(round => !RoundStates.ContainsKey(round.Key));
			var removedRoundStates = RoundStates.Where(round => !responseRoundStates.ContainsKey(round.Key));

			var roundsToUpdate = updatedRoundStates.Where(updatedRound => RoundStates[updatedRound.Key] != updatedRound.Value)
				.Union(newRoundStates)
				.Union(removedRoundStates)
				.Select(rs => rs.Key)
				.ToList();

			RoundStates = updatedRoundStates.Union(newRoundStates).ToDictionary(s => s.Key, s => s.Value);

			lock (AwaitersLock)
			{
				// Handle round independent tasks.
				if (Awaiters.TryGetValue(AnyRoundId, out var taskAndPredicateList))
				{
					foreach (var roundState in RoundStates.Values)
					{
						RemoveCompletedAwaiters(taskAndPredicateList, roundState);
					}
				}

				if (roundsToUpdate.Any())
				{
					// Handle round dependent tasks.
					foreach (var roundId in roundsToUpdate)
					{
						if (!RoundStates.TryGetValue(roundId, out var roundState))
						{
							// The round is missing.
							var tasks = Awaiters[roundId];
							foreach (var t in tasks)
							{
								t.Task.TrySetException(new InvalidOperationException($"Round {roundId} is not running anymore."));
							}
							Awaiters.Remove(roundId);
							continue;
						}

						if (roundState is not null && Awaiters.TryGetValue(roundId, out var list))
						{
							RemoveCompletedAwaiters(list, roundState);
						}
					}
				}
			}
		}

		private static void RemoveCompletedAwaiters(List<RoundStateAwaiter> taskAndPredicateList, RoundState roundState)
		{
			foreach (var taskAndPredicate in taskAndPredicateList.Where(taskAndPredicate => taskAndPredicate.Predicate(roundState)).ToArray())
			{
				// The predicate was fulfilled.
				var task = taskAndPredicate.Task;
				task.TrySetResult(roundState);
				taskAndPredicateList.Remove(taskAndPredicate);
			}
		}

		public Task<RoundState> CreateRoundAwaiter(uint256 roundId, Predicate<RoundState> predicate, CancellationToken cancellationToken)
		{
			TaskCompletionSource<RoundState> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
			List<RoundStateAwaiter>? predicateList = null;
			RoundStateAwaiter? taskAndPredicate = null;

			lock (AwaitersLock)
			{
				if (!Awaiters.ContainsKey(roundId))
				{
					Awaiters.Add(roundId, new List<RoundStateAwaiter>());
				}
				predicateList = Awaiters[roundId];

				taskAndPredicate = new RoundStateAwaiter(tcs, predicate);
				predicateList.Add(taskAndPredicate);
			}

			cancellationToken.Register(() =>
			{
				tcs.TrySetCanceled();
				lock (AwaitersLock)
				{
					predicateList.Remove(taskAndPredicate);
				}
			});

			return tcs.Task;
		}

		public Task<RoundState> CreateRoundAwaiter(Predicate<RoundState> predicate, CancellationToken cancellationToken)
		{
			// Zero denotes that the predicate should run for any round.
			return CreateRoundAwaiter(AnyRoundId, predicate, cancellationToken);
		}

		public override Task StopAsync(CancellationToken cancellationToken)
		{
			lock (AwaitersLock)
			{
				foreach (var t in Awaiters.SelectMany(a => a.Value).Select(a => a.Task))
				{
					t.TrySetCanceled(cancellationToken);
				}
			}
			return base.StopAsync(cancellationToken);
		}
	}
}
