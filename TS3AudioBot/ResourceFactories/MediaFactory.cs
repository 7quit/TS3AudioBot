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

		public RResultCode GetResource(string uri, out AudioResource resource)
		{
			return GetResourceById(uri, null, out resource);
		}

		public RResultCode GetResourceById(string id, string name, out AudioResource resource)
		{
			string outName;
			var result = ValidateUri(out outName, id);
			if (string.IsNullOrWhiteSpace(outName))
				outName = id;

			if (result == RResultCode.MediaNoWebResponse)
			{
				resource = null;
				return result;
			}
			else
			{
				resource = new MediaResource(id, name ?? outName, id, result);
				return RResultCode.Success;
			}
		}

		public string RestoreLink(string id) => id;

		private static RResultCode ValidateUri(out string name, string uri)
		{
			Uri uriResult;
			if (!Uri.TryCreate(uri, UriKind.RelativeOrAbsolute, out uriResult))
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
					return ValidateFile(out name, uri);
				else
				{
					name = null;
					return RResultCode.MediaUnknownUri;
				}
			}
			catch (InvalidOperationException)
			{
				return ValidateFile(out name, uri);
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

		public void PostProcess(PlayData data, out bool abortPlay)
		{
			MediaResource mediaResource = (MediaResource)data.Resource;
			if (mediaResource.InternalResultCode == RResultCode.Success)
			{
				abortPlay = false;
			}
			else
			{
				abortPlay = true;
				data.Session.Write(
					$"This uri might be invalid ({mediaResource.InternalResultCode}), do you want to start anyway?");
				data.Session.UserResource = data;
				data.Session.SetResponse(ResponseValidation, null);
			}
		}

		private static bool ResponseValidation(ExecutionInformation info)
		{
			Answer answer = TextUtil.GetAnswer(info.TextMessage.Message);
			if (answer == Answer.Yes)
			{
				PlayData data = info.Session.UserResource;
				info.Session.Bot.FactoryManager.Play(data);
			}
			else if (answer == Answer.No)
			{
				info.Session.UserResource = null;
			}
			return answer != Answer.Unknown;
		}

		public void Dispose()
		{

		}
	}

	public sealed class MediaResource : AudioResource
	{
		public override AudioType AudioType => AudioType.MediaLink;

		public string ResourceURL { get; private set; }
		public RResultCode InternalResultCode { get; private set; }

		public MediaResource(string id, string name, string url, RResultCode internalRC)
			: base(id, name)
		{
			ResourceURL = url;
			InternalResultCode = internalRC;
		}

		public override string Play()
		{
			return ResourceURL;
		}
	}
}
