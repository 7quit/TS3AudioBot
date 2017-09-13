// TS3Client - A free TeamSpeak3 client implementation
// Copyright (C) 2017  TS3Client contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

//#define DIAGNOSTICS
//#define DIAG_RAWPKG
//#define DIAG_TIMEOUT
//#define DIAG_RTT


namespace TS3Client.Full
{
	using System;
	using System.Diagnostics;
	using System.Collections.Generic;
	using System.IO;
	using System.Linq;
	using System.Net;
	using System.Net.Sockets;
	using System.Threading;

	public sealed class PacketHandler
	{
		/// <summary>Greatest allowed packet size, including the complete header.</summary>
		private const int MaxPacketSize = 500;
		private const int HeaderSize = 13;
		private const int MaxDecompressedSize = 1024 * 1024; // ServerDefault: 40000 (check original code again)
		private const int ReceivePacketWindowSize = 50;

		// Timout calculations
		private static readonly TimeSpan PacketTimeout = TimeSpan.FromSeconds(30);
		/// <summary>The SmoothedRoundTripTime holds the smoothed average time
		/// it takes for a packet to get ack'd.</summary>
		private TimeSpan SmoothedRtt;
		/// <summary>Holds the smoothed rtt variation.</summary>
		private TimeSpan SmoothedRttVar;
		/// <summary>Holds the current RetransmissionTimeOut, which determines the timespan until
		/// a packet is considered to be lost.</summary>
		private TimeSpan CurrentRto;
		/// <summary>Smoothing factor for the SmoothedRtt.</summary>
		private const float alphaSmooth = 0.125f;
		/// <summary>Smoothing factor for the SmoothedRttDev.</summary>
		private const float betaSmooth = 0.25f;
		/// <summary>The maximum wait time to retransmit a packet.</summary>
		private static readonly TimeSpan MaxRetryInterval = TimeSpan.FromMilliseconds(1000);
		/// <summary>The timeout check loop interval.</summary>
		private static readonly TimeSpan ClockResolution = TimeSpan.FromMilliseconds(100);
		private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(1);
		private readonly Stopwatch PingTimer = new Stopwatch();
		private ushort LastSentPingId;
		private ushort LastReceivedPingId;

		private readonly ushort[] packetCounter;
		private readonly uint[] generationCounter;
		private readonly Dictionary<ushort, OutgoingPacket> packetAckManager;
		private readonly RingQueue<IncomingPacket> receiveQueue;
		private readonly RingQueue<IncomingPacket> receiveQueueLow;
		private readonly object sendLoopLock = new object();
		private readonly AutoResetEvent sendLoopPulse = new AutoResetEvent(false);
		private readonly Ts3Crypt ts3Crypt;
		private UdpClient udpClient;
		private Thread resendThread;
		private int resendThreadId;

		public NetworkStats NetworkStats { get; }

		public ushort ClientId { get; set; }
		private IPEndPoint remoteAddress;
		public MoveReason? ExitReason { get; set; }
		private bool Closed => ExitReason != null;

		public PacketHandler(Ts3Crypt ts3Crypt)
		{
			packetAckManager = new Dictionary<ushort, OutgoingPacket>();
			receiveQueue = new RingQueue<IncomingPacket>(ReceivePacketWindowSize, ushort.MaxValue + 1);
			receiveQueueLow = new RingQueue<IncomingPacket>(ReceivePacketWindowSize, ushort.MaxValue + 1);
			NetworkStats = new NetworkStats();

			packetCounter = new ushort[9];
			generationCounter = new uint[9];
			this.ts3Crypt = ts3Crypt;
			resendThreadId = -1;
		}

		public void Connect(IPEndPoint address)
		{
			resendThread = new Thread(ResendLoop) { Name = "PacketHandler" };
			resendThreadId = resendThread.ManagedThreadId;

			lock (sendLoopLock)
			{
				ClientId = 0;
				ExitReason = null;
				SmoothedRtt = MaxRetryInterval;
				SmoothedRttVar = TimeSpan.Zero;
				CurrentRto = MaxRetryInterval;
				LastSentPingId = 0;
				LastReceivedPingId = 0;

				packetAckManager.Clear();
				receiveQueue.Clear();
				receiveQueueLow.Clear();
				Array.Clear(packetCounter, 0, packetCounter.Length);
				Array.Clear(generationCounter, 0, generationCounter.Length);

				ConnectUdpClient(address);
			}

			resendThread.Start();

			AddOutgoingPacket(ts3Crypt.ProcessInit1(null), PacketType.Init1);
		}

		private void ConnectUdpClient(IPEndPoint address)
		{
			((IDisposable)udpClient)?.Dispose();

			try
			{
				remoteAddress = address;
				udpClient = new UdpClient(remoteAddress.AddressFamily);
				udpClient.Connect(remoteAddress);
			}
			catch (SocketException ex) { throw new Ts3Exception("Could not connect", ex); }
		}

		public void Stop(MoveReason closeReason = MoveReason.LeftServer)
		{
			resendThreadId = -1;
			lock (sendLoopLock)
			{
				((IDisposable)udpClient)?.Dispose();
				if (!ExitReason.HasValue)
					ExitReason = closeReason;
				sendLoopPulse.Set();
			}
		}

		public void AddOutgoingPacket(byte[] packet, PacketType packetType, PacketFlags addFlags = PacketFlags.None)
		{
			lock (sendLoopLock)
			{
				if (Closed)
					return;

				if (NeedsSplitting(packet.Length) && packetType != PacketType.VoiceWhisper)
				{
					// VoiceWhisper packets are for some reason excluded
					if (packetType == PacketType.Voice)
						return; // Exception maybe ??? This happens when a voice packet is bigger then the allowed size

					packet = QuickLZ.Compress(packet, 1);
					addFlags |= PacketFlags.Compressed;

					if (NeedsSplitting(packet.Length))
					{
						foreach (var splitPacket in BuildSplitList(packet, packetType))
							AddOutgoingPacket(splitPacket, addFlags);
						return;
					}
				}
				AddOutgoingPacket(new OutgoingPacket(packet, packetType), addFlags);
			}
		}

		private void AddOutgoingPacket(OutgoingPacket packet, PacketFlags flags = PacketFlags.None)
		{
			lock (sendLoopLock)
			{
				var ids = GetPacketCounter(packet.PacketType);
				if (ts3Crypt.CryptoInitComplete)
					IncPacketCounter(packet.PacketType);

				packet.PacketId = ids.Id;
				packet.GenerationId = ids.Generation;
				packet.ClientId = ClientId;
				packet.PacketFlags |= flags;

				switch (packet.PacketType)
				{
				case PacketType.Voice:
				case PacketType.VoiceWhisper:
					packet.PacketFlags |= PacketFlags.Unencrypted;
					NetUtil.H2N(packet.PacketId, packet.Data, 0);
					break;

				case PacketType.Command:
				case PacketType.CommandLow:
					packet.PacketFlags |= PacketFlags.Newprotocol;
					packetAckManager.Add(packet.PacketId, packet);
					break;

				case PacketType.Ping:
					LastSentPingId = packet.PacketId;
					packet.PacketFlags |= PacketFlags.Unencrypted;

					break;
				case PacketType.Pong:
					packet.PacketFlags |= PacketFlags.Unencrypted;
					break;

				case PacketType.Ack:
				case PacketType.AckLow:
					break; // Nothing to do

				case PacketType.Init1:
					packet.PacketFlags |= PacketFlags.Unencrypted;
					packetAckManager.Add(packet.PacketId, packet);
					break;

				default: throw Util.UnhandledDefault(packet.PacketType);
				}

#if DIAGNOSTICS && DIAG_RAWPKG
				Console.WriteLine($"[OT] {packet}");
#endif

				ts3Crypt.Encrypt(packet);

				SendRaw(packet);
			}
		}

		private IdTuple GetPacketCounter(PacketType packetType)
			=> (packetType != PacketType.Init1)
				? new IdTuple(packetCounter[(int)packetType], generationCounter[(int)packetType])
				: new IdTuple(101, 0);

		private void IncPacketCounter(PacketType packetType)
		{
			packetCounter[(int)packetType]++;
			if (packetCounter[(int)packetType] == 0)
				generationCounter[(int)packetType]++;
		}

		public void CryptoInitDone()
		{
			if (!ts3Crypt.CryptoInitComplete)
				throw new InvalidOperationException($"{nameof(CryptoInitDone)} was called although it isn't initialized");
			IncPacketCounter(PacketType.Command);
		}

		private static IEnumerable<OutgoingPacket> BuildSplitList(byte[] rawData, PacketType packetType)
		{
			int pos = 0;
			bool first = true;
			bool last;

			const int maxContent = MaxPacketSize - HeaderSize;
			do
			{
				int blockSize = Math.Min(maxContent, rawData.Length - pos);
				if (blockSize <= 0) break;

				var tmpBuffer = new byte[blockSize];
				Array.Copy(rawData, pos, tmpBuffer, 0, blockSize);
				var packet = new OutgoingPacket(tmpBuffer, packetType);

				last = pos + blockSize == rawData.Length;
				if (first ^ last)
					packet.FragmentedFlag = true;
				if (first)
					first = false;

				yield return packet;
				pos += blockSize;

			} while (!last);
		}

		private static bool NeedsSplitting(int dataSize) => dataSize + HeaderSize > MaxPacketSize;

		public IncomingPacket FetchPacket()
		{
			while (true)
			{
				if (Closed)
					return null;

				if (TryFetchPacket(receiveQueue, out IncomingPacket packet))
					return packet;
				if (TryFetchPacket(receiveQueueLow, out packet))
					return packet;

				var dummy = new IPEndPoint(IPAddress.Any, 0);
				byte[] buffer;

				try { buffer = udpClient.Receive(ref dummy); }
				catch (IOException) { return null; }
				catch (SocketException) { return null; }
				if (dummy.Address.Equals(remoteAddress.Address) && dummy.Port != remoteAddress.Port)
					continue;

				packet = Ts3Crypt.GetIncommingPacket(buffer);
				// Invalid packet, ignore
				if (packet == null)
					continue;

				// check if we already have this packet and only need to ack it.
				if (IsCommandPacketSet(packet))
					continue;

				if (!ts3Crypt.Decrypt(packet))
					continue;

				NetworkStats.LogInPacket(packet);

#if DIAGNOSTICS && DIAG_RAWPKG
				Console.WriteLine($"[IN] {packet}");
#endif

				switch (packet.PacketType)
				{
				case PacketType.Voice: break;
				case PacketType.VoiceWhisper: break;
				case PacketType.Command: packet = ReceiveCommand(packet); break;
				case PacketType.CommandLow: packet = ReceiveCommand(packet); break;
				case PacketType.Ping: ReceivePing(packet); break;
				case PacketType.Pong: ReceivePong(packet); break;
				case PacketType.Ack: packet = ReceiveAck(packet); break;
				case PacketType.AckLow: break;
				case PacketType.Init1: ReceiveInitAck(); break;
				default:
					throw new ArgumentOutOfRangeException();
				}

				if (packet != null)
					return packet;
			}
		}

		#region Packet checking
		// These methods are for low level packet processing which the
		// rather high level TS3FullClient should not worry about.

		private bool IsCommandPacketSet(IncomingPacket packet)
		{
			RingQueue<IncomingPacket> packetQueue;
			if (packet.PacketType == PacketType.Command)
			{
				SendAck(packet.PacketId, PacketType.Ack);
				packetQueue = receiveQueue;
			}
			else if (packet.PacketType == PacketType.CommandLow)
			{
				SendAck(packet.PacketId, PacketType.AckLow);
				packetQueue = receiveQueueLow;
			}
			else
			{
				return false;
			}

			packet.GenerationId = packetQueue.GetGeneration(packet.PacketId);
			return packetQueue.IsSet(packet.PacketId);
		}

		private IncomingPacket ReceiveCommand(IncomingPacket packet)
		{
			RingQueue<IncomingPacket> packetQueue;
			if (packet.PacketType == PacketType.Command)
				packetQueue = receiveQueue;
			else if (packet.PacketType == PacketType.CommandLow)
				packetQueue = receiveQueueLow;
			else
				throw new InvalidOperationException("The packet is not a command");

			packetQueue.Set(packet.PacketId, packet);

			return TryFetchPacket(packetQueue, out IncomingPacket retPacket) ? retPacket : null;
		}

		private static bool TryFetchPacket(RingQueue<IncomingPacket> packetQueue, out IncomingPacket packet)
		{
			if (packetQueue.Count <= 0) { packet = null; return false; }

			int take = 0;
			int takeLen = 0;
			bool hasStart = false;
			bool hasEnd = false;
			for (int i = 0; i < packetQueue.Count; i++)
			{
				if (packetQueue.TryPeekStart(i, out IncomingPacket peekPacket))
				{
					take++;
					takeLen += peekPacket.Size;
					if (peekPacket.FragmentedFlag)
					{
						if (!hasStart) { hasStart = true; }
						else if (!hasEnd) { hasEnd = true; break; }
					}
					else
					{
						if (!hasStart) { hasStart = true; hasEnd = true; break; }
					}
				}
				else
				{
					break;
				}
			}

			if (!hasStart || !hasEnd) { packet = null; return false; }

			// GET
			if (!packetQueue.TryDequeue(out packet))
				throw new InvalidOperationException("Packet in queue got missing (?)");

			if (take > 1) // MERGE
			{
				var preFinalArray = new byte[takeLen];

				// for loop at 0th element
				int curCopyPos = packet.Size;
				Array.Copy(packet.Data, 0, preFinalArray, 0, packet.Size);

				for (int i = 1; i < take; i++)
				{
					if (!packetQueue.TryDequeue(out IncomingPacket nextPacket))
						throw new InvalidOperationException("Packet in queue got missing (?)");

					Array.Copy(nextPacket.Data, 0, preFinalArray, curCopyPos, nextPacket.Size);
					curCopyPos += nextPacket.Size;
				}
				packet.Data = preFinalArray;
			}

			// DECOMPRESS
			if (packet.CompressedFlag)
			{
				if (QuickLZ.SizeDecompressed(packet.Data) > MaxDecompressedSize)
					throw new InvalidOperationException("Compressed packet is too large");
				packet.Data = QuickLZ.Decompress(packet.Data);
			}
			return true;
		}

		private void SendAck(ushort ackId, PacketType ackType)
		{
			byte[] ackData = new byte[2];
			NetUtil.H2N(ackId, ackData, 0);
			if (ackType == PacketType.Ack || ackType == PacketType.AckLow)
				AddOutgoingPacket(ackData, ackType);
			else
				throw new InvalidOperationException("Packet type is not an Ack-type");
		}

		private IncomingPacket ReceiveAck(IncomingPacket packet)
		{
			if (packet.Data.Length < 2)
				return null;
			ushort packetId = NetUtil.N2Hushort(packet.Data, 0);

			lock (sendLoopLock)
			{
				if (packetAckManager.TryGetValue(packetId, out OutgoingPacket ackPacket))
				{
					UpdateRto(Util.Now - ackPacket.LastSendTime);
					packetAckManager.Remove(packetId);
				}
			}
			return packet;
		}

		private void SendPing()
		{
			AddOutgoingPacket(new byte[0], PacketType.Ping);
			PingTimer.Restart();
		}

		private void ReceivePing(IncomingPacket packet)
		{
			var idDiff = packet.PacketId - LastReceivedPingId;
			if (idDiff > 1 && idDiff < ReceivePacketWindowSize)
				NetworkStats.LogLostPings(idDiff - 1);
			if (idDiff > 0 || idDiff < -ReceivePacketWindowSize)
				LastReceivedPingId = packet.PacketId;
			byte[] pongData = new byte[2];
			NetUtil.H2N(packet.PacketId, pongData, 0);
			AddOutgoingPacket(pongData, PacketType.Pong);
		}

		private void ReceivePong(IncomingPacket packet)
		{
			ushort answerId = NetUtil.N2Hushort(packet.Data, 0);

			if (LastSentPingId == answerId)
			{
				var rtt = PingTimer.Elapsed;
				UpdateRto(rtt);
				NetworkStats.AddPing(rtt);
			}
		}

		public void ReceiveInitAck()
		{
			// this method is a bit hacky since it removes ALL Init1 packets
			// from the sendQueue instead of the one with the preceding
			// init step id (see Ts3Crypt.ProcessInit1).
			// But usually this should be no problem since the init order is linear
			lock (sendLoopLock)
			{
				var remPacket = packetAckManager.Values.Where(x => x.PacketType == PacketType.Init1).ToArray();
				foreach (var packet in remPacket)
					packetAckManager.Remove(packet.PacketId);
			}
		}

		#endregion

		private void UpdateRto(TimeSpan sampleRtt)
		{
			// Timeout calculation (see: https://tools.ietf.org/html/rfc6298)
			// SRTT_{i+1}    = (1-a) * SRTT_i   + a * RTT
			// DevRTT_{i+1}  = (1-b) * DevRTT_i + b * | RTT - SRTT_{i+1} |
			// Timeout_{i+1} = SRTT_{i+1} + max(ClockRes, 4 * DevRTT_{i+1})
			if (SmoothedRtt < TimeSpan.Zero)
				SmoothedRtt = sampleRtt;
			else
				SmoothedRtt = TimeSpan.FromTicks((long)((1 - alphaSmooth) * SmoothedRtt.Ticks + alphaSmooth * sampleRtt.Ticks));
			SmoothedRttVar = TimeSpan.FromTicks((long)((1 - betaSmooth) * SmoothedRttVar.Ticks + betaSmooth * Math.Abs(sampleRtt.Ticks - SmoothedRtt.Ticks)));
			CurrentRto = SmoothedRtt + Util.Max(ClockResolution, TimeSpan.FromTicks(4 * SmoothedRttVar.Ticks));
#if DIAGNOSTICS && DIAG_RTT
			Console.WriteLine("SRTT:{0} RTTVAR:{1} RTO: {2}", SmoothedRtt, SmoothedRttVar, CurrentRto);
#endif
		}

		/// <summary>
		/// ResendLoop will regularly check if a packet has be acknowleged and trys to send it again
		/// if the timeout for a packet ran out.
		/// </summary>
		private void ResendLoop()
		{
			DateTime pingCheck = Util.Now;

			while (Thread.CurrentThread.ManagedThreadId == resendThreadId)
			{
				lock (sendLoopLock)
				{
					if (Closed)
						break;

					if (packetAckManager.Count > 0 && ResendPackages(packetAckManager.Values))
					{
						Stop(MoveReason.Timeout);
						return;
					}
				}

				var now = Util.Now;
				var nextTest = pingCheck - now + PingInterval;
				// we need to check if CryptoInitComplete because while false packet ids won't be incremented
				if (nextTest < TimeSpan.Zero && ts3Crypt.CryptoInitComplete)
				{
					pingCheck += PingInterval;
					SendPing();
				}

				sendLoopPulse.WaitOne(ClockResolution);
			}
		}

		private bool ResendPackages(IEnumerable<OutgoingPacket> packetList)
		{
			var now = Util.Now;
			foreach (var outgoingPacket in packetList)
			{
				// Check if the packet timed out completely
				if (outgoingPacket.FirstSendTime < now - PacketTimeout)
				{
#if DIAGNOSTICS && DIAG_TIMEOUT
					Console.WriteLine("TIMEOUT: " + DebugUtil.DebugToHex(outgoingPacket.Raw));
#endif
					return true;
				}

				// Check if we should retransmit a packet because it probably got lost
				if (outgoingPacket.LastSendTime < now - CurrentRto)
				{
#if DIAGNOSTICS && DIAG_TIMEOUT
					Console.WriteLine("RESEND PACKET: " + DebugUtil.DebugToHex(outgoingPacket.Raw));
#endif
					CurrentRto = CurrentRto + CurrentRto;
					if (CurrentRto > MaxRetryInterval)
						CurrentRto = MaxRetryInterval;
					SendRaw(outgoingPacket);
				}
			}
			return false;
		}

		private void SendRaw(OutgoingPacket packet)
		{
			packet.FirstSendTime = packet.LastSendTime = Util.Now;
			NetworkStats.LogOutPacket(packet);
			udpClient.Send(packet.Raw, packet.Raw.Length);
		}
	}

	struct IdTuple
	{
		public ushort Id { get; set; }
		public uint Generation { get; set; }

		public IdTuple(ushort id, uint generation) { Id = id; Generation = generation; }
	}
}
