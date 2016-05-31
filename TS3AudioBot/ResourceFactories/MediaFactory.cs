// TS3AudioBot - An advanced Musicbot for Teamspeak 3
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
	using System;
	using System.IO;
	using Helper;
	using Helper.AudioTags;
	using CommandSystem;

	public sealed class MediaFactory : IResourceFactory
	{
		public AudioType FactoryFor => AudioType.MediaLink;

		public bool MatchLink(string uri) => true;

		public R<PlayResource> GetResource(string uri)
		{
			return GetResourceById(new AudioResource(uri, null, AudioType.MediaLink));
		}

		public R<PlayResource> GetResourceById(AudioResource resource)
		{
			string outName;
			var result = ValidateUri(out outName, resource.ResourceId);

			if (result != RResultCode.Success)
			{
				return result.ToString();
			}
			else
			{
				if (string.IsNullOrWhiteSpace(outName))
					outName = resource.ResourceId;
				return new PlayResource(resource.ResourceId, resource.ResourceTitle != null ? resource : resource.WithName(outName));
			}
		}

		public string RestoreLink(string id) => id;

		private static RResultCode ValidateUri(out string name, string uri)
		{
			// if the uri is a relative local path, we can resolve it to a full path
			string fullUri;
			try
			{
				fullUri = Path.GetFullPath(uri);
			}
			catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is System.Security.SecurityException)
			{
				fullUri = uri;
			}

			Uri uriResult;
			if (!Uri.TryCreate(fullUri, UriKind.RelativeOrAbsolute, out uriResult))
			{
				name = null;
				return RResultCode.MediaInvalidUri;
			}

			try
			{
				string scheme = uriResult.Scheme;
				if (scheme == Uri.UriSchemeHttp
					|| scheme == Uri.UriSchemeHttps
					|| scheme == Uri.UriSchemeFtp)
					return ValidateWeb(out name, uri);
				else if (uriResult.Scheme == Uri.UriSchemeFile)
					return ValidateFile(out name, fullUri);
				else
				{
					name = null;
					return RResultCode.MediaUnknownUri;
				}
			}
			catch (InvalidOperationException)
			{
				return ValidateFile(out name, fullUri);
			}
		}

		private static string GetStreamName(Stream stream) => AudioTagReader.GetTitle(stream);

		private static RResultCode ValidateWeb(out string name, string link)
		{
			string outName = null;
			if (WebWrapper.GetResponse(new Uri(link), response => { using (var stream = response.GetResponseStream()) outName = GetStreamName(stream); })
				== ValidateCode.Ok)
			{
				name = outName;
				return RResultCode.Success;
			}
			else
			{
				name = null;
				return RResultCode.MediaNoWebResponse;
			}
		}

		private static RResultCode ValidateFile(out string name, string path)
		{
			name = null;

			if (!File.Exists(path))
				return RResultCode.MediaFileNotFound;

			try
			{
				using (var stream = File.Open(path, FileMode.Open, FileAccess.Read))
				{
					name = GetStreamName(stream);
					return RResultCode.Success;
				}
			}
			catch (PathTooLongException) { return RResultCode.AccessDenied; }
			catch (DirectoryNotFoundException) { return RResultCode.MediaFileNotFound; }
			catch (FileNotFoundException) { return RResultCode.MediaFileNotFound; }
			catch (IOException) { return RResultCode.AccessDenied; }
			catch (UnauthorizedAccessException) { return RResultCode.AccessDenied; }
			catch (NotSupportedException) { return RResultCode.AccessDenied; }
		}

		public void Dispose()
		{

		}
	}
}
