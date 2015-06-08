﻿using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LockCheck;
using TeamSpeak3QueryApi.Net.Specialized.Responses;
using TeamSpeak3QueryApi.Net.Specialized.Notifications;

namespace TS3AudioBot
{
	class BobController : IDisposable
	{
		private const int CONNECT_TIMEOUT_MS = 10000;
		private const int CONNECT_TIMEOUT_INTERVAL_MS = 100;
		/// <summary>
		/// After TIMEOUT seconds, the bob disconnects.
		/// </summary>
		private const int BOB_TIMEOUT = 60;
		/// <summary>
		/// The name of the file which is used to tell our own server client id to the Bob.
		/// </summary>
		private const string FILENAME = "queryId";

		private BobControllerData data;
		private Task timerTask;
		private CancellationTokenSource cancellationTokenSource;
		private CancellationToken cancellationToken;
		private DateTime lastUpdate = DateTime.Now;
		private bool sending = false;

		private readonly object lockObject = new object();
		public QueryConnection QueryConnection { get; set; }
		private GetClientsInfo bobClient;

		public bool IsRunning { get; private set; }

		public bool IsTimingOut
		{
			get { return timerTask != null && !timerTask.IsCompleted; }
		}

		public bool Sending
		{
			get { return sending; }
			set
			{
				sending = value;
				SendMessage("audio " + (value ? "on" : "off"));
			}
		}

		public BobController(BobControllerData data)
		{
			this.data = data;
		}

		[LockCritical("lockObject")]
		private void SendMessage(string message)
		{
			if (bobClient == null)
			{
				Log.Write(Log.Level.Warning, "bobClient is null!");
				return;
			}
			lock (lockObject)
			{
				if (IsRunning)
				{
					QueryConnection.TSClient.SendMessage(message, bobClient);
				}
			}
		}

		public void HasUpdate()
		{
			lastUpdate = DateTime.Now;
		}

		[LockCritical("lockObject")]
		public void Start()
		{
			lock (lockObject)
			{
				if (!IsRunning)
				{
					// Write own server query id into file
					string filepath = Path.Combine(data.folder, FILENAME);
					string myId = QueryConnection.TSClient.WhoAmI().Result.ClientId.ToString();
					try
					{
						File.WriteAllText(filepath, myId, Encoding.UTF8);
					}
					catch (IOException ex)
					{
						Log.Write(Log.Level.Error, "Can't open file {0} ({1})", filepath, ex);
						return;
					}
					// register callback to know immediatly when the bob connects
					QueryConnection.OnClientConnect += AwaitBobConnect;
					if (!Util.Execute(FilePath.StartTsBot))
					{
						QueryConnection.OnClientConnect -= AwaitBobConnect;
						return;
					}
				}
			}
		}

		private void AwaitBobConnect(object sender, ClientEnterView e)
		{
			Log.Write(Log.Level.Debug, "User entere with GrId {0}", e.ServerGroups);
			if (e.ServerGroups == "15")
			{
				bobClient = QueryConnection.GetClientById(e.Id).Result;
				IsRunning = true;
				if (IsTimingOut)
					cancellationTokenSource.Cancel();
			}
		}

		public void StartEndTimer()
		{
			HasUpdate();
			if (IsRunning)
			{
				if (IsTimingOut)
				{
					cancellationTokenSource.Cancel();
					timerTask.Wait();
				}
				InternalStartEndTimer();
			}
		}

		private void InternalStartEndTimer()
		{
			cancellationTokenSource = new CancellationTokenSource();
			cancellationToken = cancellationTokenSource.Token;
			timerTask = Task.Run(() =>
				{
					try
					{
						while (!cancellationToken.IsCancellationRequested)
						{
							double inactiveSeconds = (DateTime.Now - lastUpdate).TotalSeconds;
							if (inactiveSeconds > BOB_TIMEOUT)
							{
								Stop();
								break;
							}
							else
								Task.Delay(TimeSpan.FromSeconds(BOB_TIMEOUT - inactiveSeconds), cancellationToken).Wait();
						}
					}
					catch (TaskCanceledException)
					{
					}
					catch (AggregateException)
					{
					}
				}, cancellationToken);
		}

		[LockCritical("lockObject")]
		public void Stop()
		{
			Log.Write(Log.Level.Info, "Stopping Bob");
			if (IsRunning)
			{
				// FIXME We should lock these two calls in between too
				SendMessage("exit");
				lock (lockObject)
				{
					IsRunning = false;
				}
			}
			if (IsTimingOut)
				cancellationTokenSource.Cancel();
		}

		public void Dispose()
		{
			Stop();
			if (cancellationTokenSource != null)
			{
				cancellationTokenSource.Dispose();
				cancellationTokenSource = null;
			}
		}
	}

	public struct BobControllerData
	{
		[InfoAttribute("the folder that contains the clientId file of this server query for " +
			"communication between the TS3AudioBot and the TeamSpeak3 Client plugin")]
		public string folder;
	}
}