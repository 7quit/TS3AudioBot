// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

namespace TS3AudioBot.Web.Api
{
	using CommandSystem;
	using Helper;
	using Newtonsoft.Json;
	using System;

	public class JsonValue<T> : JsonValueBase
	{
		protected Func<T, string> AsString { get; }
		protected Func<T, object> AsJson { get; }

		new public T Value => (T)base.Value;

		public JsonValue(T value) : base(value) { }
		public JsonValue(T value, string msg) : base(value, msg) { }
		public JsonValue(T value, Func<T, string> asString = null, Func<T, object> asJson = null) : base(value)
		{
			AsString = asString;
			AsJson = asJson;
		}

		public override object GetSerializeObject()
		{
			if (AsJson != null)
				return AsJson(Value);
			else
				return Value;
		}

		public override string ToString()
		{
			if (AsStringResult == null)
			{
				if (AsString != null)
					AsStringResult = AsString.Invoke(Value);
				else if (Value == null)
					AsStringResult = string.Empty;
				else
					AsStringResult = Value.ToString();
			}
			return AsStringResult;
		}
	}

	public class JsonValueBase : JsonObject
	{
		protected object Value { get; }

		public JsonValueBase(object value) : base(null) { Value = value; }
		public JsonValueBase(object value, string msg) : base(msg ?? string.Empty) { Value = value; }

		public override object GetSerializeObject() => Value;

		public override string Serialize()
		{
			var seriObj = GetSerializeObject();
			if (seriObj != null && XCommandSystem.BasicTypes.Contains(seriObj.GetType()))
				return JsonConvert.SerializeObject(this);
			if (seriObj is IJsonSerializable jsonSerializable)
				return jsonSerializable.ToJson();
			return base.Serialize();
		}

		public override string ToString()
		{
			if (AsStringResult == null)
				AsStringResult = Value?.ToString() ?? string.Empty;
			return AsStringResult;
		}
	}
}
