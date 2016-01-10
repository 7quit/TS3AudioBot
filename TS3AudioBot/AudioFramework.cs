﻿using System;
using System.Threading;
using System.Threading.Tasks;
using TS3Query.Messages;
using TS3AudioBot.RessourceFactories;

namespace TS3AudioBot
{
	class AudioFramework : IDisposable
	{
		public const int MAXUSERVOLUME = 200;
		public const int MAXVOLUME = 512;
		private const int TIMEOUT_MS = 30000;
		private const int TIMEOUT_INTERVAL_MS = 100;

		private AudioFrameworkData audioFrameworkData;
		private Task ressourceEndTask;
		/// <summary>
		/// This token is used to cancel a WaitNotifyEnd task, don't change it while the task is running!
		/// </summary>
		private CancellationTokenSource ressourceEndTokenSource;
		private CancellationToken ressourceEndToken;

		public AudioRessource currentRessource { get; protected set; }
		private IPlayerConnection playerConnection;

		public delegate void RessourceStartedDelegate(AudioRessource audioRessource);
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

		// Playermethods

		/// <summary>Jumps to the position in the audiostream if available.</summary>
		/// <param name="pos">Position in seconds from the start.</param>
		/// <returns>True if the seek request was valid, false otherwise.</returns>
		public bool Seek(int pos)
		{
			if (pos < 0 || pos > playerConnection.GetLength())
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
			playerConnection.AudioClear();
		}

		/// <summary>Starts or resumes the current song.</summary>
		public void Play()
		{
			playerConnection.AudioPlay();
		}

		// Audioframework

		/// <summary>Creates a new AudioFramework</summary>
		/// <param name="afd">Required initialization data from a ConfigFile interpreter.</param>
		public AudioFramework(AudioFrameworkData afd)
		{
			audioFrameworkData = afd;
			playerConnection = new VLCConnection(afd.vlcLocation);
			playerConnection.Start();
		}

		/// <summary>
		/// <para>Gets started at the beginning of a new ressource.</para>
		/// <para>It calls the stop event when a ressource is finished.</para>
		/// <para>This task can be cancelled by cancelling ressourceEndToken.</para>
		/// </summary>
		private async void WaitNotifyEnd()
		{
			try
			{
				Log.Write(Log.Level.Debug, "AF Wait for start");
				const int timeoutmax = TIMEOUT_MS / TIMEOUT_INTERVAL_MS;
				int timeoutcur = timeoutmax;

				while (timeoutcur-- > 0 && currentRessource != null && !ressourceEndToken.IsCancellationRequested)
				{
					if (playerConnection.IsPlaying())
					{
						timeoutcur = timeoutmax;
						await Task.Delay(TIMEOUT_MS, ressourceEndToken);
					}
					else
					{
						await Task.Delay(TIMEOUT_INTERVAL_MS, ressourceEndToken);
					}
				}
				Log.Write(Log.Level.Debug, "AF Timeout or stopped (IsPlaying:{0})", timeoutcur);
				if (!ressourceEndToken.IsCancellationRequested)
					Stop(false);
			}
			catch (TaskCanceledException) { }
			catch (AggregateException) { }
		}

		/// <summary>
		/// <para>Stops the old ressource and starts the new one.</para>
		/// <para>The volume gets resetted and the OnStartEvent gets triggered.</para>
		/// </summary>
		/// <param name="audioRessource">The audio ressource to start.</param>
		/// <returns>True if the audio ressource started successfully, false otherwise.</returns>
		public AudioResultCode StartRessource(AudioRessource audioRessource, ClientData invoker)
		{
			if (audioRessource == null)
			{
				Log.Write(Log.Level.Debug, "AF audioRessource is null");
				return AudioResultCode.NoNewRessource;
			}

			audioRessource.InvokingUser = invoker;

			Stop(true);

			playerConnection.AudioClear();

			if (audioRessource.Volume == -1)
				audioRessource.Volume = audioFrameworkData.defaultVolume;

			string ressourceLink = audioRessource.Play();
			if (string.IsNullOrWhiteSpace(ressourceLink))
				return AudioResultCode.RessouceInternalError;

			if (audioRessource.Enqueue)
			{
				//playerConnection.AudioAdd(ressourceLink);
				// TODO to playlist mgr
				audioRessource.Enqueue = false;
			}
			else
			{
				Log.Write(Log.Level.Debug, "AF ar start: {0}", audioRessource);
				playerConnection.AudioStart(ressourceLink);
				Volume = audioRessource.Volume;
				Log.Write(Log.Level.Debug, "AF set volume: {0}", Volume);
			}

			if (OnRessourceStarted != null)
				OnRessourceStarted(audioRessource);

			currentRessource = audioRessource;
			if (ressourceEndTask == null || ressourceEndTask.IsCompleted || ressourceEndTask.IsCanceled || ressourceEndTask.IsFaulted)
			{
				if (ressourceEndTask != null)
					ressourceEndTask.Dispose();
				if (ressourceEndTokenSource != null)
					ressourceEndTokenSource.Dispose();
				ressourceEndTokenSource = new CancellationTokenSource();
				ressourceEndToken = ressourceEndTokenSource.Token;
				ressourceEndTask = Task.Run((Action)WaitNotifyEnd);
			}
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
			if (currentRessource != null)
			{
				currentRessource = null;
				playerConnection.AudioStop();
				if (!ressourceEndTask.IsCompleted)
					ressourceEndTokenSource.Cancel();
				if (OnRessourceStopped != null)
					OnRessourceStopped(restart);
			}
		}

		public void Dispose()
		{
			Log.Write(Log.Level.Info, "Closing Mediaplayer...");

			Stop(false);
			if (ressourceEndTask != null)
				ressourceEndTask.Wait();

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
