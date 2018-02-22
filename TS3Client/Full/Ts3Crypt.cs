// TS3Client - A free TeamSpeak3 client implementation
// Copyright (C) 2017  TS3Client contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

namespace TS3Client.Full
{
	using Commands;
	using Helper;
	using Org.BouncyCastle.Asn1;
	using Org.BouncyCastle.Asn1.X9;
	using Org.BouncyCastle.Crypto;
	using Org.BouncyCastle.Crypto.Digests;
	using Org.BouncyCastle.Crypto.Engines;
	using Org.BouncyCastle.Crypto.Generators;
	using Org.BouncyCastle.Crypto.Modes;
	using Org.BouncyCastle.Crypto.Parameters;
	using Org.BouncyCastle.Math;
	using Org.BouncyCastle.Math.EC;
	using Org.BouncyCastle.Security;
	using Org.BouncyCastle.Crypto.EC;
	using System.Collections.Generic;
	using System;
	using System.Linq;
	using System.Security.Cryptography;
	using System.Text;

	/// <summary>Provides all cryptographic functions needed for the low- and high level TeamSpeak protocol usage.</summary>
	public sealed class Ts3Crypt
	{
		private const string DummyKeyAndNonceString = "c:\\windows\\system\\firewall32.cpl";
		private static readonly byte[] DummyKey = Encoding.ASCII.GetBytes(DummyKeyAndNonceString.Substring(0, 16));
		private static readonly byte[] DummyIv = Encoding.ASCII.GetBytes(DummyKeyAndNonceString.Substring(16, 16));
		private static readonly (byte[], byte[]) DummyKeyAndNonceTuple = (DummyKey, DummyIv);
		private static readonly byte[] Ts3InitMac = Encoding.ASCII.GetBytes("TS3INIT1");
		private static readonly byte[] Initversion = { 0x09, 0x83, 0x8C, 0xCF }; // 3.1.8 [Stable]
		private readonly EaxBlockCipher eaxCipher = new EaxBlockCipher(new AesEngine());

		private const int MacLen = 8;
		private const int PacketTypeKinds = 9;

		public IdentityData Identity { get; set; }

		internal bool CryptoInitComplete { get; private set; }
		private byte[] ivStruct;
		private readonly byte[] fakeSignature = new byte[MacLen];
		private readonly Tuple<byte[], byte[], uint>[] cachedKeyNonces = new Tuple<byte[], byte[], uint>[PacketTypeKinds * 2];

		public Ts3Crypt()
		{
			Reset();
		}

		internal void Reset()
		{
			CryptoInitComplete = false;
			ivStruct = null; // TODO recheck
			Array.Clear(fakeSignature, 0, fakeSignature.Length);
			Array.Clear(cachedKeyNonces, 0, cachedKeyNonces.Length);
			Identity = null;
		}

		#region KEY IMPORT/EXPROT
		/// <summary>This methods loads a secret identity.</summary>
		/// <param name="key">The key stored in base64, encoded like the libtomcrypt export method of a private key.
		/// Or the TS3Client's shorted private-only key.</param>
		/// <param name="keyOffset">A number which determines the security level of an identity.</param>
		/// <param name="lastCheckedKeyOffset">The last brute forced number. Default 0: will take the current keyOffset.</param>
		/// <returns>The identity information.</returns>
		public static IdentityData LoadIdentity(string key, ulong keyOffset, ulong lastCheckedKeyOffset = 0)
		{
			// Note: libtomcrypt stores the private AND public key when exporting a private key
			// This makes importing very convenient :)
			byte[] asnByteArray = Convert.FromBase64String(key);
			ImportKeyDynamic(asnByteArray, out var publicKey, out var privateKey);
			return LoadIdentity(publicKey, privateKey, keyOffset, lastCheckedKeyOffset);
		}

		private static IdentityData LoadIdentity(ECPoint publicKey, BigInteger privateKey, ulong keyOffset, ulong lastCheckedKeyOffset)
		{
			return new IdentityData(privateKey, publicKey)
			{
				ValidKeyOffset = keyOffset,
				LastCheckedKeyOffset = lastCheckedKeyOffset < keyOffset ? keyOffset : lastCheckedKeyOffset,
			};
		}

		private static readonly ECKeyGenerationParameters KeyGenParams = new ECKeyGenerationParameters(X9ObjectIdentifiers.Prime256v1, new SecureRandom());

		private static ECPoint ImportPublicKey(byte[] asnByteArray)
		{
			var asnKeyData = (DerSequence)Asn1Object.FromByteArray(asnByteArray);
			var x = ((DerInteger)asnKeyData[2]).Value;
			var y = ((DerInteger)asnKeyData[3]).Value;

			var ecPoint = KeyGenParams.DomainParameters.Curve.CreatePoint(x, y);
			return ecPoint;
		}

		private static void ImportKeyDynamic(byte[] asnByteArray, out ECPoint publicKey, out BigInteger privateKey)
		{
			privateKey = null;
			publicKey = null;
			var asnKeyData = (DerSequence)Asn1Object.FromByteArray(asnByteArray);
			var bitInfo = ((DerBitString)asnKeyData[0]).IntValue;
			if (bitInfo == 0b0000_0000 || bitInfo == 0b1000_0000)
			{
				var x = ((DerInteger)asnKeyData[2]).Value;
				var y = ((DerInteger)asnKeyData[3]).Value;
				publicKey = KeyGenParams.DomainParameters.Curve.CreatePoint(x, y);

				if (bitInfo == 0b1000_0000)
				{
					privateKey = ((DerInteger)asnKeyData[4]).Value;
				}
			}
			else if (bitInfo == 0b1100_0000)
			{
				privateKey = ((DerInteger)asnKeyData[2]).Value;
			}
		}

		internal static string ExportPublicKey(ECPoint publicKey)
		{
			var dataArray = new DerSequence(
				new DerBitString(new byte[] { 0b0000_0000 }, 7),
				new DerInteger(32),
				new DerInteger(publicKey.AffineXCoord.ToBigInteger()),
				new DerInteger(publicKey.AffineYCoord.ToBigInteger())).GetDerEncoded();
			return Convert.ToBase64String(dataArray);
		}

		internal static string ExportPrivateKey(BigInteger privateKey)
		{
			var dataArray = new DerSequence(
				new DerBitString(new byte[] { 0b1100_0000 }, 6),
				new DerInteger(32),
				new DerInteger(privateKey)).GetDerEncoded();
			return Convert.ToBase64String(dataArray);
		}

		internal static string ExportPublicAndPrivateKey(ECPoint publicKey, BigInteger privateKey)
		{
			var dataArray = new DerSequence(
				new DerBitString(new byte[] { 0b1000_0000 }, 7),
				new DerInteger(32),
				new DerInteger(publicKey.AffineXCoord.ToBigInteger()),
				new DerInteger(publicKey.AffineYCoord.ToBigInteger()),
				new DerInteger(privateKey)).GetDerEncoded();
			return Convert.ToBase64String(dataArray);
		}

		internal static string GetUidFromPublicKey(string publicKey)
		{
			var publicKeyBytes = Encoding.ASCII.GetBytes(publicKey);
			var hashBytes = Hash1It(publicKeyBytes);
			return Convert.ToBase64String(hashBytes);
		}

		internal static ECPoint RestorePublicFromPrivateKey(BigInteger privateKey)
		{
			var curve = ECNamedCurveTable.GetByOid(X9ObjectIdentifiers.Prime256v1);
			return curve.G.Multiply(privateKey).Normalize();
		}

		#endregion

		#region TS3INIT1 / CRYPTO INIT

		/// <summary>Calculates and initializes all required variables for the secure communication.</summary>
		/// <param name="alpha">The alpha key from clientinit encoded in base64.</param>
		/// <param name="beta">The beta key from clientinit encoded in base64.</param>
		/// <param name="omega">The omega key from clientinit encoded in base64.</param>
		internal void CryptoInit(string alpha, string beta, string omega)
		{
			if (Identity == null)
				throw new InvalidOperationException($"No identity has been imported or created. Use the {nameof(LoadIdentity)} or {nameof(GenerateNewIdentity)} method before.");

			var alphaBytes = Convert.FromBase64String(alpha);
			var betaBytes = Convert.FromBase64String(beta);
			var omegaBytes = Convert.FromBase64String(omega);
			var serverPublicKey = ImportPublicKey(omegaBytes);

			byte[] sharedKey = GetSharedSecret(serverPublicKey);
			SetSharedSecret(alphaBytes, betaBytes, sharedKey);

			CryptoInitComplete = true;
		}

		/// <summary>Calculates a shared secred with ECDH from the client private and server public key.</summary>
		/// <param name="publicKeyPoint">The public key of the server.</param>
		/// <returns>Returns a 32 byte shared secret.</returns>
		private byte[] GetSharedSecret(ECPoint publicKeyPoint)
		{
			ECPoint p = publicKeyPoint.Multiply(Identity.PrivateKey).Normalize();
			byte[] keyArr = p.AffineXCoord.ToBigInteger().ToByteArray();
			if (keyArr.Length == 32)
				return keyArr;
			var sharedData = new byte[32];
			if (keyArr.Length > 32)
				Array.Copy(keyArr, keyArr.Length - 32, sharedData, 0, 32);
			else // keyArr.Length < 32
				Array.Copy(keyArr, 0, sharedData, 32 - keyArr.Length, keyArr.Length);
			return sharedData;
		}

		/// <summary>Initializes all required variables for the secure communication.</summary>
		/// <param name="alpha">The alpha key from clientinit.</param>
		/// <param name="beta">The beta key from clientinit.</param>
		/// <param name="sharedKey">The omega key from clientinit.</param>
		private void SetSharedSecret(byte[] alpha, byte[] beta, byte[] sharedKey)
		{
			// prepares the ivstruct consisting of 2 random byte chains of 10 bytes which each both clients agreed on
			ivStruct = new byte[20];
			Array.Copy(alpha, 0, ivStruct, 0, 10);
			Array.Copy(beta, 0, ivStruct, 10, 10);

			// applying hashes to get the required values for ts3
			var buffer = Hash1It(sharedKey);
			XorBinary(ivStruct, buffer, 20, ivStruct);

			// creating a dummy signature which will be used on packets which dont use a real encryption signature (like plain voice)
			buffer = Hash1It(ivStruct, 0, 20);
			Array.Copy(buffer, 0, fakeSignature, 0, 8);
		}

		public static ECPoint publicTmp;
		public static BigInteger privateTmp;

		public static string ToHex(IEnumerable<byte> seq) => string.Join(" ", seq.Select(x => x.ToString("X2")));
		public static byte[] FromHex(string hex) => hex.Split(' ').Select(x => Convert.ToByte(x, 16)).ToArray();

		public static readonly X9ECParameters Ed25519Curve = CustomNamedCurves.GetByName("Curve25519");
		public static readonly ECDomainParameters Ed25519Domain = new ECDomainParameters(Ed25519Curve.Curve, Ed25519Curve.G, Ed25519Curve.N, Ed25519Curve.H, Ed25519Curve.GetSeed());
		private static readonly ECKeyGenerationParameters EdKeyGenParams = new ECKeyGenerationParameters(Ed25519Domain, new SecureRandom());

		public static void Test()
		{
			/*var dat = FromHex("20 41 11 60 A1 36 E0 DC 4E 67 03 E6 E3 6C E1 7F 3A 7A 8F 70 56 1B FD E8 EE 50 8E E5 CE C7 10 66");
			
			var pt = Ed25519.DecodeInt(dat).ToBcBi();

			var pub = Ed25519Curve.G.Multiply(pt);
			var x = Ed25519.EncodePoint(
				pub.Normalize().AffineXCoord.ToBigInteger().ToNetBi(),
				pub.Normalize().AffineYCoord.ToBigInteger().ToNetBi());
			var xstr = ToHex(x);*/

			var pk2nd = FromHex("");
			var scalar = FromHex("");

			var hash = Hash512It(pk2nd.Concat(scalar).ToArray());

			hash[0] &= 0xF8;
			hash[31] &= 0x3F;
			hash[31] |= 0x40;

			var p1 = Ed25519.DecodePoint(hash.Take(32).ToArray());
		}

		private static void GenerateTemporaryKey()
		{
			// TODO own method fn(curve) -> (q, d)
			var generator = new ECKeyPairGenerator();
			generator.Init(EdKeyGenParams);
			var keyPair = generator.GenerateKeyPair();

			var privateKey = (ECPrivateKeyParameters)keyPair.Private;
			var publicKey = (ECPublicKeyParameters)keyPair.Public;

			publicTmp = publicKey.Q;
			privateTmp = privateKey.D;
		}

		public static byte[] EncodeInt(BigInteger y)
		{
			byte[] nin = y.ToByteArray();
			var nout = new byte[Math.Max(nin.Length, 32)];
			Array.Copy(nin, nout, nin.Length);
			return nout;
		}

		internal void CryptoInit2(string license, string omega, string proof)
		{
			// TODO verify proof

			var licenseArr = Convert.FromBase64String(license);

		}

		private byte[] GetSharedSecret2(ECPoint publicKeyPoint)
		{
			throw new NotImplementedException();
		}

		private void SetSharedSecret2(byte[] alpha, byte[] beta, byte[] sharedKey)
		{

		}

		internal R<byte[]> ProcessInit1(byte[] data)
		{
			const int versionLen = 4;
			const int initTypeLen = 1;

			int? type = null;
			if (data != null)
			{
				type = data[0];
				if (data.Length < initTypeLen)
					return "Invalid Init1 packet (too short)";
			}
			byte[] sendData;

			switch (type)
			{
			case 0x7F:
			// 0x7F: Some strange servers do this
			// the normal client responds by starting again
			case null:
				sendData = new byte[versionLen + initTypeLen + 4 + 4 + 8];
				Array.Copy(Initversion, 0, sendData, 0, versionLen); // initVersion
				sendData[versionLen] = 0x00; // initType
				NetUtil.H2N(Util.UnixNow, sendData, versionLen + initTypeLen); // 4byte timestamp
				for (int i = 0; i < 4; i++)
					sendData[i + versionLen + initTypeLen + 4] = (byte)Util.Random.Next(0, 256); // 4byte random
				return sendData;

			case 1:
				switch (data.Length)
				{
				case 21:
					sendData = new byte[versionLen + initTypeLen + 16 + 4];
					Array.Copy(Initversion, 0, sendData, 0, versionLen); // initVersion
					sendData[versionLen] = 0x02; // initType
					Array.Copy(data, 1, sendData, versionLen + initTypeLen, 20);
					return sendData;
				case 5:
					var errorNum = NetUtil.N2Huint(data, 1);
					if (Enum.IsDefined(typeof(Ts3ErrorCode), errorNum))
						return $"Got Init1(1) error: {(Ts3ErrorCode)errorNum}";
					return $"Got Init1(1) undefined error code: {errorNum}";
				default:
					return "Invalid or unrecognized Init1(1) packet";
				}

			case 3:
				byte[] alphaBytes = new byte[10];
				Util.Random.NextBytes(alphaBytes);
				var alpha = Convert.ToBase64String(alphaBytes);
				string initAdd = Ts3Command.BuildToString("clientinitiv",
					new ICommandPart[] {
						new CommandParameter("alpha", alpha),
						new CommandParameter("omega", Identity.PublicKeyString),
						new CommandParameter("ot", 1),
						new CommandParameter("ip", string.Empty) });
				var textBytes = Util.Encoder.GetBytes(initAdd);

				// Prepare solution
				int level = NetUtil.N2Hint(data, initTypeLen + 128);
				var y = SolveRsaChallange(data, initTypeLen, level);
				if (!y.Ok)
					return y;

				// Copy bytes for this result: [Version..., InitType..., data..., y..., text...]
				sendData = new byte[versionLen + initTypeLen + 232 + 64 + textBytes.Length];
				// Copy this.Version
				Array.Copy(Initversion, 0, sendData, 0, versionLen);
				// Write InitType
				sendData[versionLen] = 0x04;
				// Copy data
				Array.Copy(data, initTypeLen, sendData, versionLen + initTypeLen, 232);
				// Copy y
				Array.Copy(y.Value, 0, sendData, versionLen + initTypeLen + 232 + (64 - y.Value.Length), y.Value.Length);
				// Copy text
				Array.Copy(textBytes, 0, sendData, versionLen + initTypeLen + 232 + 64, textBytes.Length);
				return sendData;

			default:
				return $"Got invalid Init1({type}) packet id";
			}
		}

		/// <summary>This method calculates x ^ (2^level) % n = y which is the solution to the server RSA puzzle.</summary>
		/// <param name="data">The data array, containing x=[0,63] and n=[64,127], each unsigned, as a BigInteger bytearray.</param>
		/// <param name="offset">The offset of x and n in the data array.</param>
		/// <param name="level">The exponent to x.</param>
		/// <returns>The y value, unsigned, as a BigInteger bytearray.</returns>
		private static R<byte[]> SolveRsaChallange(byte[] data, int offset, int level)
		{
			if (level < 0 || level > 1_000_000)
				return "RSA challange level is not within an acceptable range";

			// x is the base, n is the modulus.
			var x = new BigInteger(1, data, 00 + offset, 64);
			var n = new BigInteger(1, data, 64 + offset, 64);
			return x.ModPow(BigInteger.Two.Pow(level), n).ToByteArrayUnsigned();
		}

		#endregion

		#region ENCRYPTION/DECRYPTION

		internal void Encrypt(BasePacket packet)
		{
			if (packet.PacketType == PacketType.Init1)
			{
				FakeEncrypt(packet, Ts3InitMac);
				return;
			}
			if (packet.UnencryptedFlag)
			{
				FakeEncrypt(packet, fakeSignature);
				return;
			}

			var (key, nonce) = GetKeyNonce(packet.FromServer, packet.PacketId, packet.GenerationId, packet.PacketType);
			packet.BuildHeader();
			ICipherParameters ivAndKey = new AeadParameters(new KeyParameter(key), 8 * MacLen, nonce, packet.Header);

			byte[] result;
			int len;
			lock (eaxCipher)
			{
				eaxCipher.Init(true, ivAndKey);
				result = new byte[eaxCipher.GetOutputSize(packet.Size)];
				try
				{
					len = eaxCipher.ProcessBytes(packet.Data, 0, packet.Size, result, 0);
					len += eaxCipher.DoFinal(result, len);
				}
				catch (Exception ex) { throw new Ts3Exception("Internal encryption error.", ex); }
			}

			// result consists of [Data..., Mac...]
			// to build the final TS3/libtomcrypt we need to copy it into another order

			// len is Data.Length + Mac.Length
			packet.Raw = new byte[packet.HeaderLength + len];
			// Copy the Mac from [Data..., Mac...] to [Mac..., Header..., Data...]
			Array.Copy(result, len - MacLen, packet.Raw, 0, MacLen);
			// Copy the Header from packet.Header to [Mac..., Header..., Data...]
			Array.Copy(packet.Header, 0, packet.Raw, MacLen, packet.HeaderLength);
			// Copy the Data from [Data..., Mac...] to [Mac..., Header..., Data...]
			Array.Copy(result, 0, packet.Raw, MacLen + packet.HeaderLength, len - MacLen);
			// Raw is now [Mac..., Header..., Data...]
		}

		private static void FakeEncrypt(BasePacket packet, byte[] mac)
		{
			packet.Raw = new byte[packet.Data.Length + MacLen + packet.HeaderLength];
			// Copy the Mac from [Mac...] to [Mac..., Header..., Data...]
			Array.Copy(mac, 0, packet.Raw, 0, MacLen);
			// Copy the Header from packet.Header to [Mac..., Header..., Data...]
			packet.BuildHeader(packet.Raw.AsSpan().Slice(MacLen, packet.HeaderLength));
			// Copy the Data from packet.Data to [Mac..., Header..., Data...]
			Array.Copy(packet.Data, 0, packet.Raw, MacLen + packet.HeaderLength, packet.Data.Length);
			// Raw is now [Mac..., Header..., Data...]
		}

		internal static S2CPacket GetIncommingPacket(byte[] data)
		{
			if (data.Length < S2CPacket.HeaderLen + MacLen)
				return null;

			return new S2CPacket(data)
			{
				PacketTypeFlagged = data[MacLen + 2],
				PacketId = NetUtil.N2Hushort(data, MacLen),
			};
		}

		internal bool Decrypt(BasePacket packet)
		{
			if (packet.PacketType == PacketType.Init1)
				return FakeDecrypt(packet, Ts3InitMac);

			if (packet.UnencryptedFlag)
				return FakeDecrypt(packet, fakeSignature);

			return DecryptData(packet);
		}

		private bool DecryptData(BasePacket packet)
		{
			Array.Copy(packet.Raw, MacLen, packet.Header, 0, packet.HeaderLength);
			var (key, nonce) = GetKeyNonce(true, packet.PacketId, packet.GenerationId, packet.PacketType);
			int dataLen = packet.Raw.Length - (MacLen + packet.HeaderLength);

			ICipherParameters ivAndKey = new AeadParameters(new KeyParameter(key), 8 * MacLen, nonce, packet.Header);
			try
			{
				byte[] result;
				lock (eaxCipher)
				{
					eaxCipher.Init(false, ivAndKey);
					result = new byte[eaxCipher.GetOutputSize(dataLen + MacLen)];

					int len = eaxCipher.ProcessBytes(packet.Raw, MacLen + packet.HeaderLength, dataLen, result, 0);
					len += eaxCipher.ProcessBytes(packet.Raw, 0, MacLen, result, len);
					len += eaxCipher.DoFinal(result, len);

					if (len != dataLen)
						return false;
				}

				packet.Data = result;
			}
			catch (Exception) { return false; }
			return true;
		}

		private static bool FakeDecrypt(BasePacket packet, byte[] mac)
		{
			if (!CheckEqual(packet.Raw, 0, mac, 0, MacLen))
				return false;
			int dataLen = packet.Raw.Length - (MacLen + packet.HeaderLength);
			packet.Data = new byte[dataLen];
			Array.Copy(packet.Raw, MacLen + packet.HeaderLength, packet.Data, 0, dataLen);
			return true;
		}

		/// <summary>TS3 uses a new key and nonce for each packet sent and received. This method generates and caches these.</summary>
		/// <param name="fromServer">True if the packet is from server to client, false for client to server.</param>
		/// <param name="packetId">The id of the packet, host order.</param>
		/// <param name="generationId">Each time the packetId reaches 65535 the next packet will go on with 0 and the generationId will be increased by 1.</param>
		/// <param name="packetType">The packetType.</param>
		/// <returns>A tuple of (key, nonce)</returns>
		private (byte[] key, byte[] nonce) GetKeyNonce(bool fromServer, ushort packetId, uint generationId, PacketType packetType)
		{
			if (!CryptoInitComplete)
				return DummyKeyAndNonceTuple;

			// only the lower 4 bits are used for the real packetType
			byte packetTypeRaw = (byte)packetType;

			int cacheIndex = packetTypeRaw * (fromServer ? 1 : 2);
			if (cachedKeyNonces[cacheIndex] == null || cachedKeyNonces[cacheIndex].Item3 != generationId)
			{
				// this part of the key/nonce is fixed by the message direction and packetType

				byte[] tmpToHash = new byte[26];

				if (fromServer)
					tmpToHash[0] = 0x30;
				else
					tmpToHash[0] = 0x31;

				tmpToHash[1] = packetTypeRaw;

				Array.Copy(BitConverter.GetBytes(NetUtil.H2N(generationId)), 0, tmpToHash, 2, 4);
				Array.Copy(ivStruct, 0, tmpToHash, 6, 20);

				var result = Hash256It(tmpToHash).AsSpan();

				cachedKeyNonces[cacheIndex] = new Tuple<byte[], byte[], uint>(result.Slice(0, 16).ToArray(), result.Slice(16, 16).ToArray(), generationId);
			}

			byte[] key = new byte[16];
			byte[] nonce = new byte[16];
			Array.Copy(cachedKeyNonces[cacheIndex].Item1, 0, key, 0, 16);
			Array.Copy(cachedKeyNonces[cacheIndex].Item2, 0, nonce, 0, 16);

			// finally the first two bytes get xor'd with the packet id
			key[0] ^= unchecked((byte)(packetId >> 8));
			key[1] ^= unchecked((byte)(packetId >> 0));

			return (key, nonce);
		}

		#endregion

		#region CRYPT HELPER

		private static bool CheckEqual(byte[] a1, int a1Index, byte[] a2, int a2Index, int len)
		{
			for (int i = 0; i < len; i++)
				if (a1[i + a1Index] != a2[i + a2Index]) return false;
			return true;
		}

		private static void XorBinary(byte[] a, byte[] b, int len, byte[] outBuf)
		{
			if (a.Length < len || b.Length < len || outBuf.Length < len) throw new ArgumentException();
			for (int i = 0; i < len; i++)
				outBuf[i] = (byte)(a[i] ^ b[i]);
		}

		private static readonly SHA1Managed Sha1HashInternal = new SHA1Managed();
		private static readonly Sha256Digest Sha256Hash = new Sha256Digest();
		private static readonly Sha512Digest Sha512Hash = new Sha512Digest();
		private static byte[] Hash1It(byte[] data, int offset = 0, int len = 0) => HashItInternal(Sha1HashInternal, data, offset, len);
		private static byte[] Hash256It(byte[] data, int offset = 0, int len = 0) => HashIt(Sha256Hash, data, offset, len);
		private static byte[] Hash512It(byte[] data, int offset = 0, int len = 0) => HashIt(Sha512Hash, data, offset, len);
		private static byte[] HashItInternal(HashAlgorithm hashAlgo, byte[] data, int offset = 0, int len = 0)
		{
			lock (hashAlgo)
			{
				return hashAlgo.ComputeHash(data, offset, len == 0 ? data.Length - offset : len);
			}
		}
		private static byte[] HashIt(IDigest hashAlgo, byte[] data, int offset = 0, int len = 0)
		{
			byte[] result;
			lock (hashAlgo)
			{
				hashAlgo.Reset();
				hashAlgo.BlockUpdate(data, offset, len == 0 ? data.Length - offset : len);
				result = new byte[hashAlgo.GetDigestSize()];
				hashAlgo.DoFinal(result, 0);
			}
			return result;
		}

		public static string HashPassword(string password)
		{
			if (string.IsNullOrEmpty(password))
				return string.Empty;
			var bytes = Util.Encoder.GetBytes(password);
			var hashed = Hash1It(bytes);
			return Convert.ToBase64String(hashed);
		}

		public static byte[] Sign(BigInteger privateKey, byte[] data)
		{
			var signer = SignerUtilities.GetSigner(X9ObjectIdentifiers.ECDsaWithSha256);
			var signKey = new ECPrivateKeyParameters(privateKey, KeyGenParams.DomainParameters);
			signer.Init(true, signKey);
			signer.BlockUpdate(data, 0, data.Length);
			return signer.GenerateSignature();
		}

		public static readonly byte[] Ts3VerionSignPublicKey = Convert.FromBase64String("UrN1jX0dBE1vulTNLCoYwrVpfITyo+NBuq/twbf9hLw=");

		public static bool EdCheck(VersionSign sign)
		{
			var ver = Encoding.ASCII.GetBytes(sign.PlattformName + sign.Name);
			return Ed25519.CheckValid(Convert.FromBase64String(sign.Sign), ver, Ts3VerionSignPublicKey);
		}

		public static void VersionSelfCheck()
		{
			var versions = typeof(VersionSign).GetProperties().Where(prop => prop.PropertyType == typeof(VersionSign));
			foreach (var ver in versions)
			{
				var verObj = (VersionSign)ver.GetValue(null);
				if (!EdCheck(verObj))
					throw new Exception($"Version is invalid: {verObj}");
			}
		}

		#endregion

		#region IDENTITY & SECURITY LEVEL

		/// <summary>Equals ulong.MaxValue.ToString().Length</summary>
		private const int MaxUlongStringLen = 20;

		/// <summary><para>Tries to improve the security level of the provided identity to the new level.</para>
		/// <para>The algorithm takes approximately 2^toLevel milliseconds to calculate; so be careful!</para>
		/// This method can be canceled anytime since progress which is not enough for the next level
		/// will be saved in <see cref="IdentityData.LastCheckedKeyOffset"/> continuously.</summary>
		/// <param name="identity">The identity to improve.</param>
		/// <param name="toLevel">The targeted level.</param>
		public static void ImproveSecurity(IdentityData identity, int toLevel)
		{
			byte[] hashBuffer = new byte[identity.PublicKeyString.Length + MaxUlongStringLen];
			byte[] pubKeyBytes = Encoding.ASCII.GetBytes(identity.PublicKeyString);
			Array.Copy(pubKeyBytes, 0, hashBuffer, 0, pubKeyBytes.Length);

			identity.LastCheckedKeyOffset = Math.Max(identity.ValidKeyOffset, identity.LastCheckedKeyOffset);
			int best = GetSecurityLevel(hashBuffer, pubKeyBytes.Length, identity.ValidKeyOffset);
			while (true)
			{
				if (best >= toLevel) return;

				int curr = GetSecurityLevel(hashBuffer, pubKeyBytes.Length, identity.LastCheckedKeyOffset);
				if (curr > best)
				{
					identity.ValidKeyOffset = identity.LastCheckedKeyOffset;
					best = curr;
				}
				identity.LastCheckedKeyOffset++;
			}
		}

		public static int GetSecurityLevel(IdentityData identity)
		{
			byte[] hashBuffer = new byte[identity.PublicKeyString.Length + MaxUlongStringLen];
			byte[] pubKeyBytes = Encoding.ASCII.GetBytes(identity.PublicKeyString);
			Array.Copy(pubKeyBytes, 0, hashBuffer, 0, pubKeyBytes.Length);
			return GetSecurityLevel(hashBuffer, pubKeyBytes.Length, identity.ValidKeyOffset);
		}

		/// <summary>Creates a new TeamSpeak3 identity.</summary>
		/// <param name="securityLevel">Minimum security level this identity will have.</param>
		/// <returns>The identity information.</returns>
		public static IdentityData GenerateNewIdentity(int securityLevel = 8)
		{
			var ecp = ECNamedCurveTable.GetByName("prime256v1");
			var domainParams = new ECDomainParameters(ecp.Curve, ecp.G, ecp.N, ecp.H, ecp.GetSeed());
			var keyGenParams = new ECKeyGenerationParameters(domainParams, new SecureRandom());
			var generator = new ECKeyPairGenerator();
			generator.Init(keyGenParams);
			var keyPair = generator.GenerateKeyPair();

			var privateKey = (ECPrivateKeyParameters)keyPair.Private;
			var publicKey = (ECPublicKeyParameters)keyPair.Public;

			var identity = LoadIdentity(publicKey.Q.Normalize(), privateKey.D, 0, 0);
			ImproveSecurity(identity, securityLevel);
			return identity;
		}

		private static int GetSecurityLevel(byte[] hashBuffer, int pubKeyLen, ulong offset)
		{
			var numBuffer = new byte[MaxUlongStringLen];
			int numLen = 0;
			do
			{
				numBuffer[numLen] = (byte)('0' + (offset % 10));
				offset /= 10;
				numLen++;
			} while (offset > 0);
			for (int i = 0; i < numLen; i++)
				hashBuffer[pubKeyLen + i] = numBuffer[numLen - (i + 1)];
			byte[] outHash = Hash1It(hashBuffer, 0, pubKeyLen + numLen);

			return GetLeadingZeroBits(outHash);
		}

		private static int GetLeadingZeroBits(byte[] data)
		{
			int curr = 0;
			int i;
			for (i = 0; i < data.Length; i++)
				if (data[i] == 0) curr += 8;
				else break;
			if (i < data.Length)
				for (int bit = 0; bit < 8; bit++)
					if ((data[i] & (1 << bit)) == 0) curr++;
					else break;
			return curr;
		}

		/// <summary>
		/// This is the reference function from the TS3 Server for checking if a hashcash offset
		/// is sufficient for the required level.
		/// </summary>
		/// <param name="data">The sha1 result from the current offset calculation</param>
		/// <param name="reqLevel">The required level to reach.</param>
		/// <returns>True if the hash meets the requirement, false otherwise.</returns>
		private static bool ValidateHash(byte[] data, int reqLevel)
		{
			var levelMask = 1 << (reqLevel % 8) - 1;

			if (reqLevel < 8)
			{
				return (data[0] & levelMask) == 0;
			}
			else
			{
				var v9 = reqLevel / 8;
				var v10 = 0;
				while (data[v10] == 0)
				{
					if (++v10 >= v9)
					{
						return (data[v9] & levelMask) == 0;
					}
				}
				return false;
			}
		}

		#endregion
	}
}
