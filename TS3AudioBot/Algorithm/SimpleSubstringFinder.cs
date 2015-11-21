﻿using System.Collections.Generic;

namespace TS3AudioBot.Algorithm
{
	class SimpleSubstringFinder<T> : ISubstringSearch<T>
	{
		private List<string> keys;
		private List<T> values;
		private HashSet<string> keyHash;

		public SimpleSubstringFinder()
		{
			keys = new List<string>();
			values = new List<T>();
			keyHash = new HashSet<string>();
		}

		public void Add(string key, T value)
		{
			if (!keyHash.Contains(key))
			{
				keys.Add(key);
				keyHash.Add(key);
				values.Add(value);
			}
		}

		public IList<T> GetValues(string key)
		{
			var result = new List<T>();
			for (int i = 0; i < keys.Count; i++)
			{
				if (keys[i].Contains(key))
				{
					result.Add(values[i]);
				}
			}
			return result;
		}

		public void Clear()
		{
			keys.Clear();
			values.Clear();
			keyHash.Clear();
		}
	}
}
