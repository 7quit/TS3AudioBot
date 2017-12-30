namespace TS3Client.Full.Audio
{
	using System;

	public interface IAudioStream { }

	// TODO add static codec info

	/// <summary>Passive producer will serve audio data that must be requested manually.</summary>
	public interface IAudioPassiveProducer : IAudioStream
	{
		int Read(byte[] buffer, int offset, int length, out Meta meta);
	}
	/// <summary>Active producer will push audio to the out stream as soon as available.</summary>
	public interface IAudioActiveProducer : IAudioStream
	{
		IAudioPassiveConsumer OutStream { get; set; }
	}
	/// <summary>Passive consumer will wait for manually passed audio data.</summary>
	public interface IAudioPassiveConsumer : IAudioStream
	{
		void Write(Span<byte> data, Meta meta);
	}
	/// <summary>Active consumer will pull audio data as soon as available.</summary>
	public interface IAudioActiveConsumer : IAudioStream
	{
		IAudioPassiveProducer InStream { get; set; }
	}

	public interface IAudioPipe : IAudioPassiveConsumer, IAudioActiveProducer { }
}
