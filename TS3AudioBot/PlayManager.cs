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

namespace TS3AudioBot
{
	using Helper;
	using History;
	using ResourceFactories;
	using System;
	using TS3Client.Messages;

	public class PlayManager
	{
		private MainBot botParent;
		private IPlayerConnection PlayerConnection => botParent.PlayerConnection;
		private PlaylistManager PlaylistManager => botParent.PlaylistManager;
		private ResourceFactoryManager ResourceFactoryManager => botParent.FactoryManager;
		private HistoryManager HistoryManager => botParent.HistoryManager;

		public PlayInfoEventArgs CurrentPlayData { get; private set; }
		public bool IsPlaying => CurrentPlayData != null;

		public event EventHandler BeforeResourceStarted;
		public event EventHandler<PlayInfoEventArgs> AfterResourceStarted;
		public event EventHandler<SongEndEventArgs> BeforeResourceStopped;
		public event EventHandler AfterResourceStopped;

		public PlayManager(MainBot parent)
		{
			botParent = parent;
		}

		public R Enqueue(InvokerData invoker, AudioResource ar) => EnqueueInternal(invoker, new PlaylistItem(ar));
		public R Enqueue(InvokerData invoker, string message, AudioType? type = null)
		{
			var result = ResourceFactoryManager.Load(message, type);
			if (!result)
				return result.Message;
			return EnqueueInternal(invoker, new PlaylistItem(result.Value.BaseData));
		}
		public R Enqueue(InvokerData invoker, uint historyId) => EnqueueInternal(invoker, new PlaylistItem(historyId));

		private R EnqueueInternal(InvokerData invoker, PlaylistItem pli)
		{
			pli.Meta.ResourceOwnerDbId = invoker.DatabaseId;
			PlaylistManager.AddToFreelist(pli);

			return R.OkR;
		}

		/// <summary>Playes the passed <see cref="AudioResource"/></summary>
		/// <param name="invoker">The invoker of this resource. Used for responses and association.</param>
		/// <param name="ar">The resource to load and play.</param>
		/// <param name="meta">Allows overriding certain settings for the resource. Can be null.</param>
		/// <returns>Ok if successful, or an error message otherwise.</returns>
		public R Play(InvokerData invoker, AudioResource ar, MetaData meta = null)
		{
			var result = ResourceFactoryManager.Load(ar);
			if (!result)
				return result.Message;
			return Play(invoker, result.Value, meta ?? new MetaData());
		}
		// TODO xml doc doesnt match here
		/// <summary>Playes the passed <see cref="PlayData.PlayResource"/></summary>
		/// <param name="invoker">The invoker of this resource. Used for responses and association.</param>
		/// <param name="audioType">The associated <see cref="AudioType"/> to a factory.</param>
		/// <param name="link">The link to resolve, load and play.</param>
		/// <param name="meta">Allows overriding certain settings for the resource. Can be null.</param>
		/// <returns>Ok if successful, or an error message otherwise.</returns>
		public R Play(InvokerData invoker, string link, AudioType? type = null, MetaData meta = null)
		{
			var result = ResourceFactoryManager.Load(link, type);
			if (!result)
				return result.Message;
			return Play(invoker, result.Value, meta ?? new MetaData());
		}
		public R Play(InvokerData invoker, uint historyId, MetaData meta = null)
		{
			var getresult = HistoryManager.GetEntryById(historyId);
			if (!getresult)
				return getresult.Message;

			var loadresult = ResourceFactoryManager.Load(getresult.Value.AudioResource);
			if (!loadresult)
				return loadresult.Message;

			return Play(invoker, loadresult.Value, meta ?? new MetaData());
		}
		public R Play(InvokerData invoker, PlaylistItem item)
		{
			if (item == null)
				throw new ArgumentNullException(nameof(item));

			R lastResult = R.OkR;
			InvokerData realInvoker = CurrentPlayData?.Invoker ?? invoker;

			if (item.HistoryId.HasValue)
			{
				lastResult = Play(realInvoker, item.HistoryId.Value, item.Meta);
				if (lastResult)
					return R.OkR;
			}
			if (!string.IsNullOrWhiteSpace(item.Link))
			{
				lastResult = Play(realInvoker, item.Link, item.AudioType, item.Meta);
				if (lastResult)
					return R.OkR;
			}
			if (item.Resource != null)
			{
				lastResult = Play(realInvoker, item.Resource, item.Meta);
				if (lastResult)
					return R.OkR;
			}
			return $"Playlist item could not be played ({lastResult.Message})";
		}

		public R Play(InvokerData invoker, PlayResource play, MetaData meta)
		{
			if (!meta.FromPlaylist)
				meta.ResourceOwnerDbId = invoker.DatabaseId;

			// add optional beforestart here. maybe for blocking/interrupting etc.
			BeforeResourceStarted?.Invoke(this, new EventArgs());

			// pass the song to the AF to start it
			var result = StartResource(play, meta);
			if (!result) return result;

			// add it to our freelist for comfort
			if (!meta.FromPlaylist)
			{
				int index = PlaylistManager.InsertToFreelist(new PlaylistItem(play.BaseData, meta));
				PlaylistManager.Index = index;
			}

			// Log our resource in the history
			ulong? owner = meta.ResourceOwnerDbId ?? invoker.DatabaseId;
			HistoryManager.LogAudioResource(new HistorySaveData(play.BaseData, owner));

			CurrentPlayData = new PlayInfoEventArgs(invoker, play, meta); // TODO meta as readonly
			AfterResourceStarted?.Invoke(this, CurrentPlayData);

			return R.OkR;
		}

		private R StartResource(PlayResource playResource, MetaData config)
		{
			//PlayerConnection.AudioStop();

			if (string.IsNullOrWhiteSpace(playResource.PlayUri))
				return "Internal resource error: link is empty";

			Log.Write(Log.Level.Debug, "PM ar start: {0}", playResource);
			var result = PlayerConnection.AudioStart(playResource.PlayUri);
			if (!result)
			{
				Log.Write(Log.Level.Error, "Error return from player: {0}", result.Message);
				return $"Internal player error ({result.Message})";
			}

			PlayerConnection.Volume = config.Volume ?? AudioValues.DefaultVolume;

			return R.OkR;
		}

		public R Next(InvokerData invoker)
		{
			PlaylistItem pli = null;
			for (int i = 0; i < 10; i++)
			{
				if ((pli = PlaylistManager.Next()) == null) break;
				if (Play(invoker, pli))
					return R.OkR;
				// optional message here that playlist entry has been skipped
			}
			if (pli == null)
				return "No next song could be played";
			else
				return "A few songs failed to start, use !next to continue";
		}
		public R Previous(InvokerData invoker)
		{
			PlaylistItem pli = null;
			for (int i = 0; i < 10; i++)
			{
				if ((pli = PlaylistManager.Previous()) == null) break;
				if (Play(invoker, pli))
					return R.OkR;
				// optional message here that playlist entry has been skipped
			}
			if (pli == null)
				return "No previous song could be played";
			else
				return "A few songs failed to start, use !previous to continue";
		}

		public void SongStoppedHook(object sender, EventArgs e) => Stop(true);

		public void Stop() => Stop(false);

		private void Stop(bool songEndedByCallback = false)
		{
			BeforeResourceStopped?.Invoke(this, new SongEndEventArgs(songEndedByCallback));

			if (songEndedByCallback && CurrentPlayData != null)
			{
				R result = Next(CurrentPlayData.Invoker);
				if (result)
					return;
				Log.Write(Log.Level.Warning, nameof(SongStoppedHook) + " could not play Next: " + result.Message);
			}
			else
			{
				PlayerConnection.AudioStop();
			}

			CurrentPlayData = null;
			AfterResourceStopped?.Invoke(this, new EventArgs());
		}
	}

	public sealed class MetaData
	{
		/// <summary>Defaults to: invoker.DbId - Can be set if the owner of a song differs from the invoker.</summary>
		public ulong? ResourceOwnerDbId { get; set; } = null;
		/// <summary>Defaults to: AudioFramwork.Defaultvolume - Overrides the starting volume.</summary>
		public int? Volume { get; set; } = null;
		/// <summary>Default: false - Indicates whether the song has been requested from a playlist.</summary>
		public bool FromPlaylist { get; set; } = false;
	}

	public class SongEndEventArgs : EventArgs
	{
		public bool SongEndedByCallback { get; }
		public SongEndEventArgs(bool songEndedByCallback) { SongEndedByCallback = songEndedByCallback; }
	}

	public sealed class PlayInfoEventArgs : EventArgs
	{
		public InvokerData Invoker { get; }
		public PlayResource PlayResource { get; }
		public AudioResource ResourceData => PlayResource.BaseData;
		public MetaData MetaData { get; }

		public PlayInfoEventArgs(ClientData invoker, PlayResource playResource, MetaData meta)
			: this(new InvokerData(invoker), playResource, meta) { }

		public PlayInfoEventArgs(InvokerData invoker, PlayResource playResource, MetaData meta)
		{
			Invoker = invoker;
			PlayResource = playResource;
			MetaData = meta;
		}
	}

	public sealed class InvokerData
	{
		public ulong? Channel { get; }
		public ushort? ClientId { get; }
		public ulong? DatabaseId { get; }
		public string ClientUid { get; }

		public InvokerData(ClientData invoker)
		{
			Channel = invoker.ChannelId == 0 ? (ulong?)null : invoker.ChannelId;
			ClientId = invoker.ClientId == 0 ? (ushort?)null : invoker.ClientId;
			DatabaseId = invoker.DatabaseId == 0 ? (ulong?)null : invoker.DatabaseId;
			ClientUid = invoker.Uid;
		}

		public InvokerData(ulong? channel = null, ushort? clientId = null, ulong? databaseId = null, string clientUid = null)
		{
			Channel = channel;
			ClientId = clientId;
			DatabaseId = databaseId;
			ClientUid = clientUid;
		}

		public override int GetHashCode()
		{
			return (ClientId ?? 0)
				^ 101 * (int)(Channel ?? 0)
				^ 101 * (int)(DatabaseId ?? 0)
				^ 101 * (ClientUid?.GetHashCode() ?? 0);
		}

		public override bool Equals(object obj)
		{
			var other = obj as InvokerData;
			if (other == null)
				return false;

			return ClientId == other.ClientId
				&& DatabaseId == other.DatabaseId
				&& ClientUid == other.ClientUid
				&& Channel == other.Channel;
		}
	}

	public static class AudioValues
	{
		public const int MaxVolume = 100;

		internal static AudioFrameworkData audioFrameworkData;

		public static int MaxUserVolume => audioFrameworkData.MaxUserVolume;
		public static int DefaultVolume => audioFrameworkData.DefaultVolume;
	}

	public class AudioFrameworkData : ConfigData
	{
		[Info("The default volume a song should start with", "10")]
		public int DefaultVolume { get; set; }
		[Info("The maximum volume a normal user can request", "30")]
		public int MaxUserVolume { get; set; }
	}
}
