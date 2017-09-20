// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

namespace TS3AudioBot.Helper
{
	using System.IO;
	using System.Text;

	public class PositionedStreamReader : TextReader
	{
		private const int BufferSize = 1 << 10; // 1024

		private Stream stream;
		private Encoding encoding;
		private Decoder decoder;
		private long baseStreamPosition;
		private long baseStreamLength;

		private StringBuilder strb;
		private byte[] byteBuffer;
		private char[] charBuffer;
		private int bufferpos;
		private int readPosition;
		private int charlen;
		private int bytelen;

		public Stream BaseStream => stream;
		public int ReadPosition => readPosition;

		public PositionedStreamReader(Stream stream) : this(stream, Encoding.UTF8) { }

		public PositionedStreamReader(Stream stream, Encoding encoding)
		{
			this.stream = stream;
			this.encoding = encoding;
			decoder = encoding.GetDecoder();
			bufferpos = 0;
			readPosition = 0;
		}

		public override string ReadLine()
		{
			if (byteBuffer == null || charBuffer == null)
			{
				byteBuffer = new byte[BufferSize];
				charBuffer = new char[BufferSize];
				bufferpos = 0;
			}

			Endl endlStatus = Endl.Nothing;
			while (true)
			{
				if (bufferpos >= BufferSize || bufferpos == 0 || stream.Position != baseStreamPosition || stream.Length != baseStreamLength)
				{
					bytelen = stream.Read(byteBuffer, 0, byteBuffer.Length);
					baseStreamPosition = stream.Position;
					baseStreamLength = stream.Length;
					if (bytelen == 0)
						return FinalizeEof();
					charlen = decoder.GetChars(byteBuffer, 0, bytelen, charBuffer, 0, false);
					bufferpos = 0;
				}

				int charReadLen = 0;
				for (int i = bufferpos; i < charlen; i++)
				{
					if (charBuffer[i] == '\r')
					{
						if (endlStatus == Endl.Nothing)
						{
							charReadLen++;
							endlStatus = Endl.CrFirst;
						}
						else if (endlStatus == Endl.CrFirst)
						{
							endlStatus = Endl.CrFinal;
							break;
						}
						else if (endlStatus == Endl.Lf)
							break;
					}
					else if (charBuffer[i] == '\n')
					{
						if (endlStatus == Endl.Nothing)
						{
							charReadLen++;
							endlStatus = Endl.Lf;
							break;
						}
						else if (endlStatus == Endl.CrFirst)
						{
							charReadLen++;
							endlStatus = Endl.CrLf;
							break;
						}
						else if (endlStatus == Endl.Lf)
							break;
					}
					else
					{
						if (endlStatus == Endl.CrFirst)
						{
							endlStatus = Endl.CrFinal;
							break;
						}
						else
							charReadLen++;
					}
				}

				if (charReadLen == 0)
					return FinalizeEof();

				if (bytelen == charlen)
					readPosition += charReadLen;
				else
					readPosition += encoding.GetByteCount(charBuffer, bufferpos, charReadLen);

				int readcnt;
				switch (endlStatus)
				{
				case Endl.CrFirst:
				case Endl.Nothing:
					readcnt = charReadLen - (endlStatus == Endl.CrFirst ? 1 : 0);
					if (strb == null)
						strb = new StringBuilder(charReadLen);
					if (readcnt > 0)
						strb.Append(charBuffer, bufferpos, charReadLen);
					bufferpos = 0;
					break;
				case Endl.CrFinal:
				case Endl.Lf:
				case Endl.CrLf:
					readcnt = charReadLen - (endlStatus == Endl.CrLf ? 2 : 1);
					string retStr;
					if (strb == null)
					{
						if (readcnt > 0)
							retStr = new string(charBuffer, bufferpos, charReadLen - (endlStatus == Endl.CrLf ? 2 : 1));
						else
							retStr = string.Empty;
					}
					else
					{
						if (readcnt > 0)
							strb.Append(charBuffer, bufferpos, readcnt);
						retStr = strb.ToString();
						strb = null;
					}
					bufferpos += charReadLen;
					return retStr;
				}
			}
		}

		public void InvalidateBuffer()
		{
			baseStreamPosition = -1;
			baseStreamLength = -1;
		}

		private string FinalizeEof()
		{
			if (strb != null)
			{
				string retStr = strb.ToString();
				strb = null;
				return retStr;
			}
			else
				return null;
		}

		private enum Endl
		{
			Nothing,
			CrFirst,
			CrFinal,
			Lf,
			CrLf,
		}
	}
}
