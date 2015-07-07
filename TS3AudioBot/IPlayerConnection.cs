﻿using System;

namespace TS3AudioBot
{
	interface IPlayerConnection : IDisposable
	{
		void Start();

		void AudioAdd(string url);
		void AudioClear();
		void AudioNext();
		void AudioPlay();
		void AudioPrevious();
		void AudioStart(string url);
		void AudioStop();
		int GetLength();
		int GetPosition();
		bool IsPlaying();
		void SetLoop(bool enabled);
		void SetPosition(int position);
		void SetRepeat(bool enabled);
		void SetVolume(int value);
	}
}
