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

namespace TS3AudioBot.History
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using Helper;

	public class HistoryManager : MarshalByRefObject, IDisposable
	{
		private readonly object accessLock = new object();
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
			lock (accessLock)
				historyFile.Store(playData);
		}

		public IEnumerable<AudioLogEntry> Search(SeachQuery query)
		{
			IEnumerable<AudioLogEntry> filteredHistory = null;

			lock (accessLock)
			{
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
		}

		public string SearchParsed(SeachQuery query)
		{
			var aleList = Search(query);
			return Formatter.ProcessQuery(aleList, SmartHistoryFormatter.DefaultAleFormat);
		}

		public AudioLogEntry GetEntryById(uint id)
		{
			lock (accessLock)
				return historyFile.GetEntryById(id);
		}

		public void RemoveEntry(AudioLogEntry ale)
		{
			if (ale == null)
				throw new ArgumentNullException(nameof(ale));
			lock (accessLock)
				historyFile.LogEntryRemove(ale);
		}

		public void RenameEntry(AudioLogEntry ale, string newName)
		{
			if (ale == null)
				throw new ArgumentNullException(nameof(ale));
			if (string.IsNullOrWhiteSpace(newName))
				throw new ArgumentNullException(nameof(newName));
			lock (accessLock)
				historyFile.LogEntryRename(ale, newName);
		}

		public void CleanHistoryFile()
		{
			lock (accessLock)
				historyFile.CleanFile();
		}

		public void Dispose()
		{
			if (historyFile != null)
			{
				lock (accessLock)
				{
					if (historyFile != null)
					{
						historyFile.Dispose();
						historyFile = null;
					}
				}
			}
		}
	}

	public struct HistoryManagerData
	{
		[Info("the absolute or relative path to the history database file")]
		public string historyFile;
	}
}