﻿using System;
using TS3Query.Messages;
using TS3AudioBot.ResourceFactories;
using TS3AudioBot.Helper;

namespace TS3AudioBot
{
	class AudioFramework : IDisposable
	{
		public int MaxUserVolume => audioFrameworkData.maxUserVolume;
		public const int MAXVOLUME = 100;
		private const int TIMEOUT_MS = 30000;
		private const int TIMEOUT_INTERVAL_MS = 100;

		private AudioFrameworkData audioFrameworkData;
		private TickWorker waitEndTick;
		private DateTime endTime;

		public PlayData CurrentPlayData { get; protected set; }
		private IPlayerConnection playerConnection;

		public delegate void RessourceStartedDelegate(PlayData audioRessource);
		public delegate void RessourceStoppedDelegate(bool restart);
		public event RessourceStartedDelegate OnRessourceStarted;
		public event RessourceStoppedDelegate OnRessourceStopped;

		// Playerproperties

		/// <summary>Loop state for the entire playlist.</summary>
		public bool Loop // TODO
		{
			get { return false; }
			set { }
		}

		/// <summary>Loop state for the current song.</summary>
		public bool Repeat { get { return playerConnection.Repeated; } set { playerConnection.Repeated = value; } }
		public int Volume { get { return playerConnection.Volume; } set { playerConnection.Volume = value; } }
		/// <summary>Starts or resumes the current song.</summary>
		public bool Pause { get { return playerConnection.Pause; } set { playerConnection.Pause = value; } }

		// Playermethods

		/// <summary>Jumps to the position in the audiostream if available.</summary>
		/// <param name="pos">Position in seconds from the start.</param>
		/// <returns>True if the seek request was valid, false otherwise.</returns>
		public bool Seek(int pos)
		{
			if (pos < 0 || pos > playerConnection.Length)
				return false;
			playerConnection.Position = pos;
			return true;
		}

		/// <summary>Plays the next song in the playlist.</summary>
		public void Next()
		{

		}

		/// <summary>Plays the previous song in the playlist.</summary>
		public void Previous()
		{

		}

		/// <summary>Clears the current playlist</summary>
		public void Clear()
		{

		}

		// Audioframework

		/// <summary>Creates a new AudioFramework</summary>
		/// <param name="afd">Required initialization data from a ConfigFile interpreter.</param>
		public AudioFramework(AudioFrameworkData afd, IPlayerConnection audioBackend)
		{
			waitEndTick = TickPool.RegisterTick(NotifyEnd, TIMEOUT_INTERVAL_MS, false);
			audioFrameworkData = afd;
			playerConnection = audioBackend;
			playerConnection.Initialize();
		}

		/// <summary>
		/// <para>Gets started at the beginning of a new ressource.</para>
		/// <para>It calls the stop event when a ressource is finished.</para>
		/// <para>Is used for player backends which are not supporting an end callback.</para>
		/// </summary>
		private void NotifyEnd()
		{
			if (endTime < DateTime.Now)
			{
				if (playerConnection.IsPlaying)
				{
					int playtime = playerConnection.Length;
					int position = playerConnection.Position;

					int endspan = playtime - position;
					endTime = DateTime.Now.AddSeconds(endspan);
				}
				else if (endTime.AddMilliseconds(TIMEOUT_MS) < DateTime.Now)
				{
					Log.Write(Log.Level.Debug, "AF Song ended with default timeout");
					Stop(false);
					waitEndTick.Active = false;
				}
			}
		}

		/// <summary>
		/// <para>Do NOT call this method directly! Use the FactoryManager instead.</para>
		/// <para>Stops the old ressource and starts the new one.</para>
		/// <para>The volume gets resetted and the OnStartEvent gets triggered.</para>
		/// </summary>
		/// <param name="audioRessource">The audio ressource to start.</param>
		/// <returns>An infocode on what happened.</returns>
		public AudioResultCode StartRessource(PlayData playData)
		{
			if (playData == null || playData.Ressource == null)
			{
				Log.Write(Log.Level.Debug, "AF audioRessource is null");
				return AudioResultCode.NoNewRessource;
			}

			Stop(true);

			string ressourceLink = playData.Ressource.Play();
			if (string.IsNullOrWhiteSpace(ressourceLink))
				return AudioResultCode.RessouceInternalError;

			Log.Write(Log.Level.Debug, "AF ar start: {0}", playData.Ressource);
			playerConnection.AudioStart(ressourceLink);

			if (playData.Volume == -1)
				Volume = audioFrameworkData.defaultVolume;
			else
				Volume = playData.Volume;
			Log.Write(Log.Level.Debug, "AF set volume: {0}", Volume);

			if (OnRessourceStarted != null)
				OnRessourceStarted(playData);

			CurrentPlayData = playData;
			endTime = DateTime.Now;
			waitEndTick.Active = true;
			return AudioResultCode.Success;
		}

		public void Stop()
		{
			Stop(false);
		}

		/// <summary>Stops the currently played song.</summary>
		/// <param name="restart">When set to true, the AudioBob won't be notified aubout the stop.
		/// Use this parameter to prevent fast off-on switching.</param>
		private void Stop(bool restart)
		{
			Log.Write(Log.Level.Debug, "AF stop old (restart:{0})", restart);
			if (CurrentPlayData != null)
			{
				CurrentPlayData = null;
				playerConnection.AudioStop();
				if (OnRessourceStopped != null)
					OnRessourceStopped(restart);
			}
		}

		public void Dispose()
		{
			Log.Write(Log.Level.Info, "Closing Mediaplayer...");

			Stop(false);

			if (playerConnection != null)
			{
				playerConnection.Dispose();
				playerConnection = null;
				Log.Write(Log.Level.Debug, "AF playerConnection disposed");
			}
		}
	}

	public struct AudioFrameworkData
	{
		//[InfoAttribute("the absolute or relative path to the local music folder")]
		//public string localAudioPath;
		[Info("the default volume a song should start with")]
		public int defaultVolume;
		[Info("the maximum volume a normal user can request")]
		public int maxUserVolume;
		[Info("the location of the vlc player (if the vlc backend is used)", "vlc")]
		public string vlcLocation;
	}

	enum AudioType
	{
		MediaLink,
		Youtube,
		Soundcloud,
	}

	enum AudioResultCode
	{
		Success,
		NoNewRessource,
		RessouceInternalError,
	}
}
