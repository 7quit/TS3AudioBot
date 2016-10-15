﻿namespace TS3Client.Full
{
	using System;
	using System.Collections.Generic;
	using System.Net.Sockets;
	using Messages;

	public sealed class TS3FullClient : TS3BaseClient
	{
		private readonly UdpClient udpClient;
		private readonly TS3Crypt ts3Crypt;
		private readonly PacketHandler packetHandler;

		private int returnCode;

		public override ClientType ClientType => ClientType.Full;

		public TS3FullClient(EventDispatchType dispatcher) : base(dispatcher)
		{
			udpClient = new UdpClient();
			ts3Crypt = new TS3Crypt();
			packetHandler = new PacketHandler(ts3Crypt, udpClient);

			returnCode = 0;
		}

		protected override void ConnectInternal(ConnectionData conData)
		{
			Reset();

			packetHandler.Start();

			try { udpClient.Connect(conData.Hostname, conData.Port); }
			catch (SocketException ex) { throw new TS3CommandException(new CommandError(), ex); }

			ts3Crypt.LoadIdentity(conData.PrivateKey, conData.KeyOffset, conData.LastCheckedKeyOffset);
			var initData = ts3Crypt.ProcessInit1(null);
			packetHandler.AddOutgoingPacket(initData, PacketType.Init1);
		}

		protected override void DisconnectInternal()
		{
			// TODO send quit message
			udpClient.Close();
		}

		protected override void NetworkLoop()
		{
			while (true)
			{
				var packet = packetHandler.FetchPacket();
				if (packet == null) break;

				switch (packet.PacketType)
				{
					case PacketType.Command:
						string message = Util.Encoder.GetString(packet.Data, 0, packet.Data.Length);
						if (!SpecialCommandProcess(message))
							ProcessCommand(message);
						break;

					case PacketType.Readable:
						// VOICE

						break;

					case PacketType.Init1:
						var forwardData = ts3Crypt.ProcessInit1(packet.Data);
						packetHandler.AddOutgoingPacket(forwardData, PacketType.Init1);
						break;
				}
			}
			Status = TS3ClientStatus.Disconnected;
		}

		private bool SpecialCommandProcess(string message)
		{
			if (message.StartsWith("initivexpand ", StringComparison.Ordinal)
				|| message.StartsWith("initserver ", StringComparison.Ordinal)
				|| message.StartsWith("channellist ", StringComparison.Ordinal)
				|| message.StartsWith("channellistfinished ", StringComparison.Ordinal))
			{
				var notification = CommandDeserializer.GenerateNotification(message);
				InvokeEvent(notification.Item1, notification.Item2);
				return true;
			}
			return false;
		}

		protected override void ProcessInitIvExpand(InitIvExpand initIvExpand)
		{
			ts3Crypt.CryptoInit(initIvExpand.Alpha, initIvExpand.Beta, initIvExpand.Omega);
			packetHandler.CryptoInitDone();
			ClientInit(
				ConnectionData.UserName,
				"Windows",
				true, true,
				string.Empty, string.Empty,
				ConnectionData.Password,
				string.Empty, string.Empty, string.Empty, "123,456",
				VersionSign.VER_3_0_19_03);
		}

		protected override void ProcessInitServer(InitServer initServer)
		{
			packetHandler.ClientId = initServer.ClientId;
			ConnectDone();
		}

		protected override IEnumerable<IResponse> SendCommand(TS3Command com, Type targetType)
		{
			if (com.ExpectResponse)
				com.AppendParameter(new CommandParameter("return_code", returnCode));

			using (WaitBlock wb = new WaitBlock(targetType))
			{
				lock (LockObj)
				{
					if (com.ExpectResponse)
					{
						RequestQueue.Enqueue(wb);
						returnCode++;
					}

					byte[] data = Util.Encoder.GetBytes(com.ToString());
					packetHandler.AddOutgoingPacket(data, PacketType.Command);
				}

				if (com.ExpectResponse)
					return wb.WaitForMessage();
				else
					return null;
			}
		}

		protected override void Reset()
		{
			base.Reset();

			ts3Crypt.Reset();
			packetHandler.Reset();

			returnCode = 0;
		}

		#region FULLCLIENT SPECIFIC COMMANDS

		public void ClientInit(string nickname, string plattform, bool inputHardware, bool outputHardware,
				string defaultChannel, string defaultChannelPassword, string serverPassword, string metaData,
				string nicknamePhonetic, string defaultToken, string hwid, VersionSign versionSign)
			=> SendNoResponsed(
				new TS3Command("clientinit", new List<CommandParameter>() {
					new CommandParameter("client_nickname", nickname),
					new CommandParameter("client_version", versionSign.Name),
					new CommandParameter("client_platform", plattform),
					new CommandParameter("client_input_hardware", inputHardware),
					new CommandParameter("client_output_hardware", outputHardware),
					new CommandParameter("client_default_channel", defaultChannel),
					new CommandParameter("client_default_channel_password", defaultChannelPassword),
					new CommandParameter("client_server_password", serverPassword),
					new CommandParameter("client_meta_data", metaData),
					new CommandParameter("client_version_sign", versionSign.Sign),
					new CommandParameter("client_key_offset", ts3Crypt.Identity.ValidKeyOffset),
					new CommandParameter("client_nickname_phonetic", nicknamePhonetic),
					new CommandParameter("client_default_token", defaultToken),
					new CommandParameter("hwid", hwid) }));
		#endregion
	}
}
