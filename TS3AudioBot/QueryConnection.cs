﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TeamSpeak3QueryApi.Net.Specialized;
using TeamSpeak3QueryApi.Net.Specialized.Notifications;
using TeamSpeak3QueryApi.Net.Specialized.Responses;
using System.Threading;

namespace TS3AudioBot
{
	class QueryConnection
	{
		private QueryConnectionData connectionData;
		private const int PingEverySeconds = 60;

		public TeamSpeakClient TSClient { get; protected set; }

		public Action<TextMessage> Callback { get; set; }

		private bool connected = false;
		private IReadOnlyList<GetClientsInfo> clientbuffer;
		private bool clientbufferoutdated = true;

		private Task keepAliveTask;
		private CancellationTokenSource keepAliveTokenSource;
		private CancellationToken keepAliveToken;

		public QueryConnection(QueryConnectionData qcd)
		{
			connectionData = qcd;
			TSClient = new TeamSpeakClient(connectionData.host);
		}

		public async void Connect()
		{
			if (!connected)
			{
				await TSClient.Connect();
				await TSClient.Login(connectionData.user, connectionData.passwd);
				await TSClient.UseServer(1);

				await TSClient.RegisterServerNotification();
				await TSClient.RegisterTextPrivateNotification();
				await TSClient.RegisterTextServerNotification();

				TSClient.Subscribe<TextMessage>(data => data.ForEach(ProcessMessage));
				TSClient.Subscribe<ClientEnterView>(data => clientbufferoutdated = true);
				TSClient.Subscribe<ClientLeftView>(data => clientbufferoutdated = true);

				connected = true;

				keepAliveTokenSource = new CancellationTokenSource();
				keepAliveToken = keepAliveTokenSource.Token;
				keepAliveTask = Task.Run((Action)KeepAlivePoke);
			}
		}

		private void KeepAlivePoke()
		{
			try
			{
				while (!keepAliveToken.IsCancellationRequested)
				{
					TSClient.WhoAmI();
					Task.Delay(TimeSpan.FromSeconds(PingEverySeconds), keepAliveToken).Wait();
				}
			}
			catch (TaskCanceledException)
			{
			}
			catch (AggregateException)
			{
			}
		}

		private void ProcessMessage(TextMessage tm)
		{
			if (Callback != null)
				Callback(tm);
		}

		public void Close()
		{
			if (connected)
			{
				connected = false;
				Console.WriteLine("Closing Queryconnection...");
				if (keepAliveToken.CanBeCanceled)
					keepAliveTokenSource.Cancel();
				if (!keepAliveTask.IsCompleted)
					keepAliveTask.Wait();
			}
		}

		public async Task<GetClientsInfo> GetClientById(int uid)
		{
			if (clientbufferoutdated)
			{
				clientbuffer = await TSClient.GetClients();
				clientbufferoutdated = false;
			}
			return clientbuffer.FirstOrDefault(client => client.Id == uid);
		}
	}

	public struct QueryConnectionData
	{
		[InfoAttribute("the address of the TeamSpeak3 Query")]
		public string host;
		[InfoAttribute("the user for the TeamSpeak3 Query")]
		public string user;
		[InfoAttribute("the password for the TeamSpeak3 Query")]
		public string passwd;
	}

	internal static class ReadOnlyCollectionExtensions
	{
		public static void ForEach<T>(this IReadOnlyCollection<T> collection, Action<T> action)
		{
			if (action == null)
				throw new ArgumentNullException("action");
			foreach (var i in collection)
				action(i);
		}
	}
}