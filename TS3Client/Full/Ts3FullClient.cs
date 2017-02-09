﻿// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2016  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

namespace TS3Client.Full
{
	using Commands;
	using Messages;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Net;
	using System.Net.Sockets;

	public sealed class Ts3FullClient : Ts3BaseClient
	{
		private UdpClient udpClient;
		private readonly Ts3Crypt ts3Crypt;
		private readonly PacketHandler packetHandler;

		private int returnCode;
		private bool wasExit;

		public override ClientType ClientType => ClientType.Full;
		public ushort ClientId => packetHandler.ClientId;
		public string QuitMessage { get; set; } = "Disconnected";
		public VersionSign VersionSign { get; set; } = VersionSign.VER_LIN_3_0_19_4;

		public Ts3FullClient(EventDispatchType dispatcher) : base(dispatcher)
		{
			ts3Crypt = new Ts3Crypt();
			packetHandler = new PacketHandler(ts3Crypt);

			wasExit = false;
			returnCode = 0;
		}

		protected override void ConnectInternal(ConnectionData conData)
		{
			var conDataFull = conData as ConnectionDataFull;
			if (conDataFull == null) throw new ArgumentException($"Use the {nameof(ConnectionDataFull)} deriverate to connect with the full client.", nameof(conData));
			if (conDataFull.Identity == null) throw new ArgumentNullException(nameof(conDataFull.Identity));

			udpClient = new UdpClient();
			packetHandler.Start(udpClient);

			try
			{
				var hostEntry = Dns.GetHostEntry(conData.Hostname);
				var ipAddr = hostEntry.AddressList.FirstOrDefault();
				if (ipAddr == null) throw new Ts3Exception("Could not resove DNS.");
				packetHandler.RemoteAddress = new IPEndPoint(ipAddr, conData.Port);
				udpClient.Connect(packetHandler.RemoteAddress);
			}
			catch (SocketException ex) { throw new Ts3Exception("Could not connect", ex); }

			ts3Crypt.Identity = conDataFull.Identity;

			packetHandler.AddOutgoingPacket(ts3Crypt.ProcessInit1(null), PacketType.Init1);
		}

		protected override void DisconnectInternal()
		{
			ClientDisconnect(MoveReason.LeftServer, QuitMessage);
		}

		protected override void InvokeEvent(IEnumerable<INotification> notification, NotificationType notifyType)
		{
			// we need to check for clientleftview to know when we disconnect from the server
			if (notifyType == NotificationType.ClientLeftView
				&& notification.Cast<ClientLeftView>().Any(clv => clv.ClientId == packetHandler.ClientId))
			{
				FullDisconnect();
				return;
			}

			base.InvokeEvent(notification, notifyType);
		}

		private void FullDisconnect()
		{
			wasExit = true;
			packetHandler.Stop();
			DisconnectDone(packetHandler.ExitReason ?? MoveReason.LeftServer); // TODO ??
		}

		protected override void NetworkLoop()
		{
			while (true)
			{
				if (wasExit)
					break;
				var packet = packetHandler.FetchPacket();
				if (packet == null)
					break;

				switch (packet.PacketType)
				{
				case PacketType.Command:
					string message = Util.Encoder.GetString(packet.Data, 0, packet.Data.Length);
					if (!SpecialCommandProcess(message))
						ProcessCommand(message);
					break;

				case PacketType.Readable:
				case PacketType.Voice:
					// VOICE

					break;

				case PacketType.Init1:
					var forwardData = ts3Crypt.ProcessInit1(packet.Data);
					packetHandler.AddOutgoingPacket(forwardData, PacketType.Init1);
					break;
				}
			}

			FullDisconnect();
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
				ConnectionData.Username,
				true, true,
				string.Empty, string.Empty,
				Ts3Crypt.HashPassword(ConnectionData.Password),
				string.Empty, string.Empty, string.Empty, "123,456",
				VersionSign);
		}

		protected override void ProcessInitServer(InitServer initServer)
		{
			lock (LockObj)
			{
				packetHandler.ClientId = initServer.ClientId;
				packetHandler.ReceiveInitAck();
				ConnectDone();
			}
		}

		protected override IEnumerable<IResponse> SendCommand(Ts3Command com, Type targetType)
		{
			if (com.ExpectResponse)
				com.AppendParameter(new CommandParameter("return_code", returnCode));

			using (var wb = new WaitBlock(targetType))
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
			packetHandler.Stop();

			returnCode = 0;
			wasExit = false;
		}

		#region FULLCLIENT SPECIFIC COMMANDS

		public void ClientInit(string nickname, bool inputHardware, bool outputHardware,
				string defaultChannel, string defaultChannelPassword, string serverPassword, string metaData,
				string nicknamePhonetic, string defaultToken, string hwid, VersionSign versionSign)
			=> SendNoResponsed(
				new Ts3Command("clientinit", new List<CommandParameter>() {
					new CommandParameter("client_nickname", nickname),
					new CommandParameter("client_version", versionSign.Name),
					new CommandParameter("client_platform", versionSign.PlattformName),
					new CommandParameter("client_input_hardware", inputHardware),
					new CommandParameter("client_output_hardware", outputHardware),
					new CommandParameter("client_default_channel", defaultChannel),
					new CommandParameter("client_default_channel_password", defaultChannelPassword), // base64(sha1(pass))
					new CommandParameter("client_server_password", serverPassword), // base64(sha1(pass))
					new CommandParameter("client_meta_data", metaData),
					new CommandParameter("client_version_sign", versionSign.Sign),
					new CommandParameter("client_key_offset", ts3Crypt.Identity.ValidKeyOffset),
					new CommandParameter("client_nickname_phonetic", nicknamePhonetic),
					new CommandParameter("client_default_token", defaultToken),
					new CommandParameter("hwid", hwid) }));

		public void ClientDisconnect(MoveReason reason, string reasonMsg)
			=> SendNoResponsed(
				new Ts3Command("clientdisconnect", new List<CommandParameter>() {
					new CommandParameter("reasonid", (int)reason),
					new CommandParameter("reasonmsg", reasonMsg) }));

		public void SendAudio(byte[] buffer, int length, Codec codec)
		{
			// [X,X,Y,DATA]
			// > X is a ushort in H2N order of a own audio packet counter
			//     it seem it can be the same as the packet counter so we will let the packethandler do it.
			// > Y is the codec byte (see Enum)
			byte[] tmpBuffer = new byte[length + 3];
			tmpBuffer[2] = (byte)codec;
			Array.Copy(buffer, 0, tmpBuffer, 3, length);
			buffer = tmpBuffer;

			packetHandler.AddOutgoingPacket(buffer, PacketType.Readable);
		}

		public void SendAudioWhisper(byte[] buffer, int length, Codec codec, IList<ulong> channelIds, IList<ushort> clientIds)
		{
			// [X,X,Y,N,M,(U,U,U,U,U,U,U,U)*,(T,T)*,DATA]
			// > X is a ushort in H2N order of a own audio packet counter
			//     it seems it can be the same as the packet counter so we will let the packethandler do it.
			// > Y is the codec byte (see Enum)
			// > N is a byte, the count of ChannelIds to send to
			// > M is a byte, the count of ClientIds to send to
			// > U is a ulong in H2N order of each targeted channelId, U is repeated N times
			// > T is a ushort in H2N order of each targeted clientId, T is repeated M times
			int offset = 2 + 1 + 2 + channelIds.Count * 8 + clientIds.Count * 2;
			byte[] tmpBuffer = new byte[length + offset];
			tmpBuffer[2] = (byte)codec;
			tmpBuffer[3] = (byte)channelIds.Count;
			tmpBuffer[4] = (byte)clientIds.Count;
			for (int i = 0; i < channelIds.Count; i++)
				NetUtil.H2N(channelIds[i], tmpBuffer, 5 + (i * 8));
			for (int i = 0; i < clientIds.Count; i++)
				NetUtil.H2N(clientIds[i], tmpBuffer, 5 + channelIds.Count * 8 + (i * 2));
			Array.Copy(buffer, 0, tmpBuffer, offset, length);
			buffer = tmpBuffer;

			packetHandler.AddOutgoingPacket(buffer, PacketType.Voice);
		}
		#endregion
	}
}
