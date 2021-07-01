using System;
using System.Diagnostics;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using WalletWasabi.Blockchain.Blocks;
using WalletWasabi.Fluent.Helpers;
using WalletWasabi.Models;
using WalletWasabi.Wallets;

namespace WalletWasabi.Fluent.ViewModels.Wallets
{
	public partial class LoadingViewModel : ActivatableViewModel
	{
		private readonly Wallet _wallet;

		[AutoNotify] private double _percent;
		[AutoNotify] private string? _statusText;
		[AutoNotify] private bool _isBackendConnected;

		private uint? _startingFilterIndex;
		private Stopwatch? _stopwatch;
		private bool _isLoading;
		private uint _filtersToSyncCount;
		private uint _filtersToProcessCount;

		public LoadingViewModel(Wallet wallet)
		{
			_wallet = wallet;
			_statusText = "";
			_percent = 0;
			_isBackendConnected = Services.Synchronizer.BackendStatus == BackendStatus.Connected;
		}

		private uint TotalCount => _filtersToProcessCount + _filtersToSyncCount;

		private uint RemainingFiltersToSync => (uint) Services.BitcoinStore.SmartHeaderChain.HashesLeft;

		protected override void OnActivated(CompositeDisposable disposables)
		{
			base.OnActivated(disposables);

			_stopwatch ??= Stopwatch.StartNew();

			Observable.Interval(TimeSpan.FromSeconds(1))
				.ObserveOn(RxApp.MainThreadScheduler)
				.Subscribe(_ =>
				{
					var downloadedFilters = _filtersToSyncCount - RemainingFiltersToSync;

					uint processedFilters = 0;
					if (Services.BitcoinStore.SmartHeaderChain.TipHeight is { } tipHeight &&
					    _wallet.LastProcessedFilter?.Header?.Height is { } lastProcessedFilterHeight)
					{
						processedFilters = _filtersToProcessCount - (tipHeight - lastProcessedFilterHeight);
					}

					var processedCount = downloadedFilters + processedFilters;

					// Console.WriteLine($"Total: {TotalCount} Processed:{processedCount} Downloaded: {downloadedFilters} ProcessedF: {processedFilters}");

					UpdateStatus(processedCount, _stopwatch.ElapsedMilliseconds);
				})
				.DisposeWith(disposables);

			if (!_isLoading)
			{
				Services.Synchronizer.WhenAnyValue(x => x.BackendStatus)
					.ObserveOn(RxApp.MainThreadScheduler)
					.Subscribe(status => IsBackendConnected = status == BackendStatus.Connected)
					.DisposeWith(disposables);

				this.RaisePropertyChanged(nameof(IsBackendConnected));

				Observable.FromEventPattern<bool>(Services.Synchronizer, nameof(Services.Synchronizer.ResponseArrivedIsGenSocksServFail))
					.Select(x => x.EventArgs)
					.Where(x => x)
					.Subscribe(async _ => await LoadWalletAsync(syncFilters: false))
					.DisposeWith(disposables);

				this.WhenAnyValue(x => x.IsBackendConnected)
					.Where(x => x)
					.Subscribe(async _ => await LoadWalletAsync(syncFilters: true))
					.DisposeWith(disposables);
			}
		}

		private async Task LoadWalletAsync(bool syncFilters)
		{
			if (_isLoading)
			{
				return;
			}

			_isLoading = true;

			SetInitValues();

			if (syncFilters)
			{
				while (RemainingFiltersToSync > 0)
				{
					await Task.Delay(1000);
				}
			}

			await UiServices.WalletManager.LoadWalletAsync(_wallet);
		}

		private void SetInitValues()
		{
			_filtersToSyncCount = (uint) Services.BitcoinStore.SmartHeaderChain.HashesLeft;

			if (Services.BitcoinStore.SmartHeaderChain.ServerTipHeight is { } tipHeight)
			{
				var startingHeight = SmartHeader.GetStartingHeader(_wallet.Network).Height;
				var bestHeight = (uint) _wallet.KeyManager.GetBestHeight().Value;

				_filtersToProcessCount = tipHeight - (bestHeight < startingHeight ? startingHeight : bestHeight);
			}
		}

		private void UpdateStatus(uint allFilters, uint processedFilters, double elapsedMilliseconds)
		{
			var percent = (decimal) processedFilters / allFilters * 100;
			_startingFilterIndex ??= processedFilters; // Store the filter index we started on. It is needed for better remaining time calculation.
			var realProcessedFilters = processedFilters - _startingFilterIndex.Value;
			var remainingFilterCount = allFilters - processedFilters;

			var tempPercent = (uint) Math.Round(percent);

			if (tempPercent == 0 || realProcessedFilters == 0 || remainingFilterCount == 0)
			{
				return;
			}

			Percent = tempPercent;
			var percentText = $"{Percent}% completed";

			var remainingMilliseconds = elapsedMilliseconds / realProcessedFilters * remainingFilterCount;
			var userFriendlyTime = TextHelpers.TimeSpanToFriendlyString(TimeSpan.FromMilliseconds(remainingMilliseconds));
			var remainingTimeText = string.IsNullOrEmpty(userFriendlyTime) ? "" : $"- {userFriendlyTime} remaining";

			StatusText = $"{percentText} {remainingTimeText}";
		}
	}
}
