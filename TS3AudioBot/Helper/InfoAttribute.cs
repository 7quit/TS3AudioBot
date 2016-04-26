namespace TS3AudioBot.Helper
{
	using System;

	[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
	internal sealed class InfoAttribute : Attribute
	{
		public bool HasDefault { get { return DefaultValue != null; } }
		public string Description { get; private set; }
		public string DefaultValue { get; private set; }

		public InfoAttribute(string description)
		{
			Description = description;
			DefaultValue = null;
		}

		public InfoAttribute(string description, string defaultValue)
		{
			Description = description;
			DefaultValue = defaultValue;
		}
	}
}
