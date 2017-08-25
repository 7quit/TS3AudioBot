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

namespace TS3AudioBot.ResourceFactories
{
	using Helper;
	using System;
	using System.Collections.Generic;
	using System.Collections.Specialized;
	using System.Globalization;
	using System.Linq;
	using System.Text;
	using System.Text.RegularExpressions;
	using System.Web;
	using System.Drawing;

	public sealed class YoutubeFactory : IResourceFactory, IPlaylistFactory, IThumbnailFactory
	{
		private static readonly Regex IdMatch = new Regex(@"((&|\?)v=|youtu\.be\/)([a-zA-Z0-9\-_]+)", Util.DefaultRegexConfig);
		private static readonly Regex LinkMatch = new Regex(@"^(https?\:\/\/)?(www\.|m\.)?(youtube\.|youtu\.be)", Util.DefaultRegexConfig);
		private static readonly Regex ListMatch = new Regex(@"(&|\?)list=([\w-]+)", Util.DefaultRegexConfig);

		public string SubCommandName => "youtube";
		public AudioType FactoryFor => AudioType.Youtube;

		private YoutubeFactoryData data;

		public YoutubeFactory(YoutubeFactoryData yfd)
		{
			data = yfd;
		}

		public bool MatchLink(string link) => LinkMatch.IsMatch(link) || ListMatch.IsMatch(link);
		bool IResourceFactory.MatchLink(string link) => LinkMatch.IsMatch(link);
		bool IPlaylistFactory.MatchLink(string link) => ListMatch.IsMatch(link);

		public R<PlayResource> GetResource(string ytLink)
		{
			Match matchYtId = IdMatch.Match(ytLink);
			if (!matchYtId.Success)
				return RResultCode.YtIdNotFound.ToString();
			return GetResourceById(new AudioResource(matchYtId.Groups[3].Value, null, AudioType.Youtube));
		}

		public R<PlayResource> GetResourceById(AudioResource resource)
		{
			if (!WebWrapper.DownloadString(out string resulthtml, new Uri($"http://www.youtube.com/get_video_info?video_id={resource.ResourceId}&el=info")))
				return RResultCode.NoConnection.ToString();

			var videoTypes = new List<VideoData>();
			NameValueCollection dataParse = HttpUtility.ParseQueryString(resulthtml);

			string videoDataUnsplit = dataParse["url_encoded_fmt_stream_map"];
			if (videoDataUnsplit != null)
			{
				string[] videoData = videoDataUnsplit.Split(',');

				foreach (string vdat in videoData)
				{
					NameValueCollection videoparse = HttpUtility.ParseQueryString(vdat);

					string vLink = videoparse["url"];
					if (vLink == null)
						continue;

					string vType = videoparse["type"];
					if (vType == null)
						continue;

					string vQuality = videoparse["quality"];
					if (vQuality == null)
						continue;

					var vt = new VideoData()
					{
						Link = vLink,
						Codec = GetCodec(vType),
						Qualitydesciption = vQuality
					};
					videoTypes.Add(vt);
				}
			}

			videoDataUnsplit = dataParse["adaptive_fmts"];
			if (videoDataUnsplit != null)
			{
				string[] videoData = videoDataUnsplit.Split(',');

				foreach (string vdat in videoData)
				{
					NameValueCollection videoparse = HttpUtility.ParseQueryString(vdat);

					string vType = videoparse["type"];
					if (vType == null)
						continue;

					bool audioOnly = false;
					if (vType.StartsWith("video/", StringComparison.Ordinal))
						continue;
					else if (vType.StartsWith("audio/", StringComparison.Ordinal))
						audioOnly = true;

					string vLink = videoparse["url"];
					if (vLink == null)
						continue;

					var vt = new VideoData()
					{
						Codec = GetCodec(vType),
						Qualitydesciption = vType,
						Link = vLink
					};
					if (audioOnly)
						vt.AudioOnly = true;
					else
						vt.VideoOnly = true;
					videoTypes.Add(vt);
				}
			}

			// Validation Process

			if (videoTypes.Count <= 0)
				return RResultCode.YtNoVideosExtracted.ToString();

			int codec = SelectStream(videoTypes);
			if (codec < 0)
				return "No playable codec found";

			var result = ValidateMedia(videoTypes[codec]);
			if (!result)
			{
				if (string.IsNullOrWhiteSpace(data.YoutubedlPath))
					return result.Message;

				return YoutubeDlWrapped(resource);
			}

			return new PlayResource(videoTypes[codec].Link, resource.ResourceTitle != null ? resource : resource.WithName(dataParse["title"] ?? $"<YT - no title : {resource.ResourceTitle}>"));
		}

		public string RestoreLink(string id) => "https://youtu.be/" + id;

		private int SelectStream(List<VideoData> list)
		{
#if DEBUG
			var dbg = new StringBuilder("YT avail codecs: ");
			foreach (var yd in list)
				dbg.Append(yd.Qualitydesciption).Append(" @ ").Append(yd.Codec).Append(", ");
			Log.Write(Log.Level.Debug, dbg.ToString());
#endif

			int autoselectIndex = list.FindIndex(t => t.Codec == VideoCodec.M4A);
			if (autoselectIndex == -1)
				autoselectIndex = list.FindIndex(t => t.AudioOnly);
			if (autoselectIndex == -1)
				autoselectIndex = list.FindIndex(t => !t.VideoOnly);

			return autoselectIndex;
		}

		private static R ValidateMedia(VideoData media)
		{
			var vcode = WebWrapper.GetResponse(new Uri(media.Link), TimeSpan.FromSeconds(1));

			switch (vcode)
			{
			case ValidateCode.Ok: return R.OkR;
			case ValidateCode.Restricted: return "The video cannot be played due to youtube restrictions.";
			case ValidateCode.Timeout: return "No connection could be established to youtube. Please try again later.";
			case ValidateCode.UnknownError: return "Unknown error occoured";
			default: throw new InvalidOperationException();
			}
		}

		private static VideoCodec GetCodec(string type)
		{
			string lowtype = type.ToLower(CultureInfo.InvariantCulture);
			bool audioOnly = false;
			string codecSubStr;
			if (lowtype.StartsWith("video/", StringComparison.Ordinal))
				codecSubStr = lowtype.Substring("video/".Length);
			else if (lowtype.StartsWith("audio/", StringComparison.Ordinal))
			{
				codecSubStr = lowtype.Substring("audio/".Length);
				audioOnly = true;
			}
			else return VideoCodec.Unknown;

			string extractedCodec;
			int codecEnd;
			extractedCodec = (codecEnd = codecSubStr.IndexOf(';')) >= 0 ? codecSubStr.Substring(0, codecEnd) : codecSubStr;

			switch (extractedCodec)
			{
			case "mp4":
				if (audioOnly)
					return VideoCodec.M4A;
				return VideoCodec.MP4;
			case "x-flv":
				return VideoCodec.FLV;
			case "3gpp":
				return VideoCodec.ThreeGP;
			case "webm":
				return VideoCodec.WEBM;
			default:
				return VideoCodec.Unknown;
			}
		}

		public R<Playlist> GetPlaylist(string url)
		{
			Match matchYtId = ListMatch.Match(url);
			if (!matchYtId.Success)
				return "Could not extract a playlist id";

			string id = matchYtId.Groups[2].Value;
			var plist = new Playlist(id);

			string nextToken = null;
			do
			{
				var queryString =
					new Uri("https://www.googleapis.com/youtube/v3/playlistItems"
							+ "?part=contentDetails,snippet"
							+ "&maxResults=50"
							+ "&playlistId=" + id
							+ "&fields=" + Uri.EscapeDataString("items(contentDetails/videoId,snippet/title),nextPageToken")
							+ (nextToken != null ? ("&pageToken=" + nextToken) : string.Empty)
							+ "&key=" + data.ApiKey);

				if (!WebWrapper.DownloadString(out string response, queryString))
					return "Web response error";
				var parsed = Util.Serializer.Deserialize<JSON_PlaylistItems>(response);
				var videoItems = parsed.items;
				YoutubePlaylistItem[] itemBuffer = new YoutubePlaylistItem[videoItems.Length];
				for (int i = 0; i < videoItems.Length; i++)
				{
					itemBuffer[i] = new YoutubePlaylistItem(new AudioResource(
							videoItems[i].contentDetails.videoId,
							videoItems[i].snippet.title,
							AudioType.Youtube));
				}

#if getlength
				queryString = new Uri($"https://www.googleapis.com/youtube/v3/videos?id={string.Join(",", itemBuffer.Select(item => item.Resource.ResourceId))}&part=contentDetails&key={data.apiKey}");
				if (!WebWrapper.DownloadString(out response, queryString))
					return "Web response error";
				var parsedTime = (Dictionary<string, object>)Util.Serializer.DeserializeObject(response);
				var videoDicts = ((object[])parsedTime["items"]).Cast<Dictionary<string, object>>().ToArray();
				for (int i = 0; i < videoDicts.Length; i++)
					itemBuffer[i].Length = XmlConvert.ToTimeSpan((string)(((Dictionary<string, object>)videoDicts[i]["contentDetails"])["duration"]));
#endif

				plist.AddRange(itemBuffer);

				nextToken = parsed.nextPageToken;
			} while (nextToken != null);

			return plist;
		}

		public static string LoadAlternative(string id)
		{
			if (!WebWrapper.DownloadString(out string resulthtml, new Uri($"https://www.youtube.com/watch?v={id}&gl=US&hl=en&has_verified=1&bpctr=9999999999")))
				return "No con";

			int indexof = resulthtml.IndexOf("ytplayer.config =");
			int ptr = indexof;
			while (resulthtml[ptr] != '{') ptr++;
			int start = ptr;
			int stackcnt = 1;
			while (stackcnt > 0)
			{
				ptr++;
				if (resulthtml[ptr] == '{') stackcnt++;
				else if (resulthtml[ptr] == '}') stackcnt--;
			}

			var jsonobj = Util.Serializer.DeserializeObject(resulthtml.Substring(start, ptr - start + 1));
			var args = GetDictVal(jsonobj, "args");
			var url_encoded_fmt_stream_map = GetDictVal(args, "url_encoded_fmt_stream_map");
			if (url_encoded_fmt_stream_map == null)
				return "No Data";

			string[] enco_split = ((string)url_encoded_fmt_stream_map).Split(',');
			foreach (var single_enco in enco_split)
			{
				var lis = HttpUtility.ParseQueryString(single_enco);

				var signature = lis["s"];
				var url = lis["url"];
				if (!url.Contains("signature"))
					url += "&signature=" + signature;
				return url;
			}
			return "No match";
		}

		private static object GetDictVal(object dict, string field) => (dict as Dictionary<string, object>)?[field];

		private R<PlayResource> YoutubeDlWrapped(AudioResource resource)
		{
			string title = null;
			string url = null;

			Log.Write(Log.Level.Debug, "YT Ruined!");

			var result = YoutubeDlHelper.FindAndRunYoutubeDl(resource.ResourceId);
			if (!result.Ok)
				return result.Message;

			var response = result.Value;
			title = response.Item1;
			var urlOptions = response.Item2;

			if (urlOptions.Count == 1)
			{
				url = urlOptions[0];
			}
			else if (urlOptions.Count >= 1)
			{
				Uri[] uriList = urlOptions.Select(s => new Uri(s)).ToArray();
				Uri bestMatch = uriList
					.FirstOrDefault(u => HttpUtility.ParseQueryString(u.Query)
						.GetValues("mime")
						.Any(x => x.StartsWith("audio", StringComparison.OrdinalIgnoreCase)));
				url = (bestMatch ?? uriList[0]).OriginalString;
			}

			if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(url))
				return "No youtube-dl response";

			Log.Write(Log.Level.Debug, "YT Saved!");
			return new PlayResource(url, resource.WithName(title));
		}

		public R<Image> GetThumbnail(PlayResource playResource)
		{
			if (!WebWrapper.DownloadString(out string response,
				new Uri($"https://www.googleapis.com/youtube/v3/videos?part=snippet&id={playResource.BaseData.ResourceId}&key={data.ApiKey}")))
				return "No connection";
			var parsed = Util.Serializer.Deserialize<JSON_PlaylistItems>(response);

			var imgurl = new Uri(parsed.items[0].snippet.thumbnails.medium.url);
			Image img = null;
			var resresult = WebWrapper.GetResponse(imgurl, (webresp) =>
			{
				using (var stream = webresp.GetResponseStream())
				{
					img = Image.FromStream(stream);
				}
			});
			if (resresult != ValidateCode.Ok)
				return "Error while reading image";
			return img;
		}

		public void Dispose() { }

#pragma warning disable CS0649
		private class JSON_PlaylistItems
		{
			public string nextPageToken;
			public JSON_Item[] items;

			public class JSON_Item
			{
				public JSON_ContentDetails contentDetails;
				public JSON_Snippet snippet;

				public class JSON_ContentDetails
				{
					public string videoId;
				}

				public class JSON_Snippet
				{
					public string title;
					public JSON_ThumbnailList thumbnails;

					public class JSON_ThumbnailList
					{
						public JSON_Thumbnail @default;
						public JSON_Thumbnail medium;
						public JSON_Thumbnail high;
						public JSON_Thumbnail standard;
						public JSON_Thumbnail maxres;

						public class JSON_Thumbnail
						{
							public string url;
							public int heigth;
							public int width;
						}
					}
				}
			}
		}
#pragma warning restore CS0649
	}

#pragma warning disable CS0649
	public class YoutubeFactoryData : ConfigData
	{
		[Info("A youtube apiv3 'Browser' type key", "AIzaSyBOqG5LUbGSkBfRUoYfUUea37-5xlEyxNs")]
		public string ApiKey { get; set; }
		[Info("Path to the youtube-dl binary or local git repository", "")]
		public string YoutubedlPath { get; set; }
	}
#pragma warning restore CS0649

	public sealed class VideoData
	{
		public string Link { get; set; }
		public string Qualitydesciption { get; set; }
		public VideoCodec Codec { get; set; }
		public bool AudioOnly { get; set; } = false;
		public bool VideoOnly { get; set; } = false;

		public override string ToString() => $"{Qualitydesciption} @ {Codec} - {Link}";
	}

	internal class YoutubePlaylistItem : PlaylistItem
	{
		public TimeSpan Length { get; set; }

		public YoutubePlaylistItem(AudioResource resource) : base(resource) { }
	}

	public enum VideoCodec
	{
		Unknown,
		MP4,
		M4A,
		WEBM,
		FLV,
		ThreeGP,
	}
}
