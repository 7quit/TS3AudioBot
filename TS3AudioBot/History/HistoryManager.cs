namespace TS3AudioBot.History
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using Helper;

	public class HistoryManager : MarshalByRefObject, IDisposable
	{
		private HistoryFile historyFile;
		private IEnumerable<AudioLogEntry> lastResult;
		public IHistoryFormatter Formatter { get; private set; }
		public uint HighestId => historyFile.CurrentID - 1;

		public HistoryManager(HistoryManagerData hmd)
		{
			Formatter = new SmartHistoryFormatter();
			historyFile = new HistoryFile();
			historyFile.OpenFile(hmd.historyFile);
		}

		public void LogAudioResource(object sender, PlayData playData)
		{
			historyFile.Store(playData);
		}

		public IEnumerable<AudioLogEntry> Search(SeachQuery query)
		{
			IEnumerable<AudioLogEntry> filteredHistory = null;

			if (!string.IsNullOrEmpty(query.TitlePart))
			{
				filteredHistory = historyFile.SearchTitle(query.TitlePart);
				if (query.UserId != null)
					filteredHistory = filteredHistory.Where(ald => ald.UserInvokeId == query.UserId.Value);
				if (query.LastInvokedAfter != null)
					filteredHistory = filteredHistory.Where(ald => ald.Timestamp > query.LastInvokedAfter.Value);
			}
			else if (query.UserId != null)
			{
				filteredHistory = historyFile.SeachByUser(query.UserId.Value);
				if (query.LastInvokedAfter != null)
					filteredHistory = filteredHistory.Where(ald => ald.Timestamp > query.LastInvokedAfter.Value);
			}
			else if (query.LastInvokedAfter != null)
			{
				filteredHistory = historyFile.SeachTillTime(query.LastInvokedAfter.Value);
			}
			else if (query.MaxResults >= 0)
			{
				lastResult = historyFile.GetLastXEntrys(query.MaxResults);
				return lastResult;
			}

			lastResult = filteredHistory;
			return filteredHistory.TakeLast(query.MaxResults);
		}

		public string SearchParsed(SeachQuery query)
		{
			var aleList = Search(query);
			return Formatter.ProcessQuery(aleList, SmartHistoryFormatter.DefaultAleFormat);
		}

		public AudioLogEntry GetEntryById(uint id) => historyFile.GetEntryById(id);

		public string RemoveEntryById(uint id)
		{
			try { historyFile.LogEntryRemove(id); return null; }
			catch (ArgumentOutOfRangeException) { return "Id not found"; }
		}

		public string RenameEntryById(uint id, string newName)
		{
			if (string.IsNullOrWhiteSpace(newName))
				return "The new name must not be null, empty or only whitespaces";
			try { historyFile.LogEntryRemove(id); return null; }
			catch (ArgumentOutOfRangeException) { return "Id not found"; }
		}

		public void Dispose()
		{
			if (historyFile != null)
			{
				historyFile.Dispose();
				historyFile = null;
			}
		}
	}

	public struct HistoryManagerData
	{
		[Info("the absolute or relative path to the history database file")]
		public string historyFile;
	}
}