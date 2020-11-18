﻿using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using DynamicData;
using NBitcoin;
using ReactiveUI;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Hwi;
using WalletWasabi.Hwi.Models;
using WalletWasabi.Logging;
using WalletWasabi.Wallets;
using HardwareWalletViewModel = WalletWasabi.Gui.Tabs.WalletManager.HardwareWallets.HardwareWalletViewModel;

namespace WalletWasabi.Fluent.ViewModels.AddWallet
{
	public class ConnectHardwareWalletViewModel : RoutableViewModel
	{
		private readonly string _walletName;
		private readonly WalletManager _walletManager;
		private readonly HwiClient _hwiClient;
		private readonly Task _detectionTask;
		private CancellationTokenSource _searchHardwareWalletCts;
		private HardwareWalletViewModel? _selectedHardwareWallet;

		public ConnectHardwareWalletViewModel(NavigationStateViewModel navigationState, string walletName, Network network, WalletManager walletManager)
			: base(navigationState, NavigationTarget.DialogScreen)
		{
			_walletName = walletName;
			_walletManager = walletManager;
			_hwiClient = new HwiClient(network);
			_detectionTask = new Task(StartHardwareWalletDetection);
			_searchHardwareWalletCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
			HardwareWallets = new ObservableCollection<HardwareWalletViewModel>();

			OpenBrowserCommand = ReactiveCommand.CreateFromTask<string>(IoHelpers.OpenBrowserAsync);

			var nextCommandIsExecute =
				this.WhenAnyValue(x => x.SelectedHardwareWallet)
					.Select(x => x?.HardwareWalletInfo.Fingerprint is { } && x.HardwareWalletInfo.IsInitialized());
			NextCommand = ReactiveCommand.Create(ConnectSelectedHardwareWallet,nextCommandIsExecute);

			this.WhenAnyValue(x => x.SelectedHardwareWallet)
				.Where(x => x is { } && x.HardwareWalletInfo.Model != HardwareWalletModels.Coldcard && !x.HardwareWalletInfo.IsInitialized())
				.Subscribe(async x =>
				{
					// TODO: Notify the user to check the device
					using var ctsSetup = new CancellationTokenSource(TimeSpan.FromMinutes(21));

					// Trezor T doesn't require interactive mode.
					var interactiveMode = !(x!.HardwareWalletInfo.Model == HardwareWalletModels.Trezor_T || x.HardwareWalletInfo.Model == HardwareWalletModels.Trezor_T_Simulator);

					await _hwiClient.SetupAsync(x.HardwareWalletInfo.Model, x.HardwareWalletInfo.Path, interactiveMode, ctsSetup.Token);
				});

			this.WhenNavigatedTo(() => Disposable.Create(_searchHardwareWalletCts.Cancel));

			_detectionTask.Start();
		}

		public HardwareWalletViewModel? SelectedHardwareWallet
		{
			get => _selectedHardwareWallet;
			set => this.RaiseAndSetIfChanged(ref _selectedHardwareWallet, value);
		}

		public ObservableCollection<HardwareWalletViewModel> HardwareWallets { get; }

		public ICommand NextCommand { get; }

		public ReactiveCommand<string, Unit> OpenBrowserCommand { get; }

		public string UDevRulesLink => "https://github.com/bitcoin-core/HWI/tree/master/hwilib/udev";

		private async void ConnectSelectedHardwareWallet()
		{
			// TODO: canExecute checks for null, this is just preventing warning
			if (SelectedHardwareWallet?.HardwareWalletInfo.Fingerprint is null)
			{
				return;
			}

			try
			{
				await StopDetection();

				using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

				var fingerPrint = (HDFingerprint)SelectedHardwareWallet.HardwareWalletInfo.Fingerprint;
				var extPubKey = await _hwiClient.GetXpubAsync(SelectedHardwareWallet.HardwareWalletInfo.Model, SelectedHardwareWallet.HardwareWalletInfo.Path, KeyManager.DefaultAccountKeyPath, cts.Token);
				var path = _walletManager.WalletDirectories.GetWalletFilePaths(_walletName).walletFilePath;

				_walletManager.AddWallet(KeyManager.CreateNewHardwareWalletWatchOnly(fingerPrint, extPubKey, path));

				// Close dialog
				ClearNavigation();
			}
			catch (Exception ex)
			{
				Logger.LogError(ex);

				// Restart detection
				_detectionTask.Start();
			}
		}

		private Task StopDetection() => Task.Run(() =>
		{
			_searchHardwareWalletCts.Cancel();

			while (!_detectionTask.IsCompleted)
			{
				Thread.Sleep(100);
			}
		});

		private async void StartHardwareWalletDetection()
		{
			while (!_searchHardwareWalletCts.IsCancellationRequested)
			{
				try
				{
					// Reset token
					_searchHardwareWalletCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

					var detectedHardwareWallets = (await _hwiClient.EnumerateAsync(_searchHardwareWalletCts.Token)).Select(x => new HardwareWalletViewModel(x)).ToList();

					// Remove wallets that are already added to software
					var walletsToRemove = detectedHardwareWallets.Where(wallet => _walletManager.GetWallets().Any(x => x.KeyManager.MasterFingerprint == wallet.HardwareWalletInfo.Fingerprint));
					detectedHardwareWallets.RemoveMany(walletsToRemove);

					// Remove disconnected hardware wallets from the list
					HardwareWallets.RemoveMany(HardwareWallets.Except(detectedHardwareWallets));

					// Remove detected wallets that are already in the list.
					detectedHardwareWallets.RemoveMany(HardwareWallets);

					// All remained detected hardware wallet is new so add.
					HardwareWallets.AddRange(detectedHardwareWallets);
				}
				catch (Exception ex) when (!(ex is OperationCanceledException))
				{
					Logger.LogError(ex);
				}
			}
		}
	}
}