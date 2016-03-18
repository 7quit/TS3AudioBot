namespace TS3AudioBot
{
	using System;
	using System.Collections.Generic;
	using System.Reflection;
	using System.Reflection.Emit;
	using System.IO;
	using System.Linq;
	using System.Text;
	using Helper;
	using CommandSystem;
	using System.Linq.Expressions;

	public class PluginManager : IDisposable
	{
		private MainBot mainBot;
		private PluginManagerData pluginManagerData;
		private Dictionary<string, Plugin> plugins;
		private HashSet<int> usedIds;

		public PluginManager(MainBot bot, PluginManagerData pmd)
		{
			if (bot == null)
				throw new ArgumentNullException(nameof(bot));

			mainBot = bot;
			pluginManagerData = pmd;
			Util.Init(ref plugins);
			Util.Init(ref usedIds);
		}

		private void CheckAndClearPlugins()
		{
			ClearMissingFiles();
			CheckLocalPlugins();
		}

		/// <summary>Updates the plugin dictinary with new and changed plugins.</summary>
		private void CheckLocalPlugins()
		{
			var dir = new DirectoryInfo(Path.Combine(Environment.CurrentDirectory, pluginManagerData.PluginPath));
			foreach (var file in dir.EnumerateFiles())
			{
				Plugin plugin;
				if (plugins.TryGetValue(file.Name, out plugin))
				{
					if (plugin.status == PluginStatus.Disabled || plugin.status == PluginStatus.Active)
						continue;
					else if (plugin.status == PluginStatus.Ready || plugin.status == PluginStatus.Off)
					{
						UnloadPlugin(plugin, false);
						plugin.Prepare();
					}
				}
				else
				{
					plugin = new Plugin(file, mainBot);
					plugin.Id = GetFreeId();

					if (plugin.Prepare() != PluginResponse.Ok)
						continue;

					plugins.Add(file.Name, plugin);
				}
			}
		}

		/// <summary>Unloads all Plugins which have no corresponding file anymore and removes and removes the from the index list.</summary>
		private void ClearMissingFiles()
		{
			// at first find all missing files
			var missingFiles = plugins.Where(kvp => !File.Exists(kvp.Value.file.FullName)).ToArray();

			foreach (var misFile in missingFiles)
			{
				// unload if it is loaded and remove
				usedIds.Remove(misFile.Value.Id);
				UnloadPlugin(misFile.Value, true);
				plugins.Remove(misFile.Key);
			}
		}

		public PluginResponse LoadPlugin(string identifier)
		{
			CheckLocalPlugins();

			int num;
			Plugin plugin;

			if (int.TryParse(identifier, out num))
			{
				plugin = plugins.Select(kvp => kvp.Value).FirstOrDefault(p => p.Id == num);
				return LoadPlugin(plugin);
			}

			if (plugins.TryGetValue(identifier, out plugin))
				return LoadPlugin(plugin);

			plugin = plugins.Select(kvp => kvp.Value).FirstOrDefault(p => p.proxy?.Name == identifier);
			return LoadPlugin(plugin);
		}

		private PluginResponse LoadPlugin(Plugin plugin)
		{
			if (plugin == null)
				return PluginResponse.PluginNotFound;

			if (plugin.status == PluginStatus.Off || plugin.status == PluginStatus.Disabled)
			{
				var response = plugin.Prepare();
				if (response != PluginResponse.Ok)
					return response;
			}

			if (plugin.status == PluginStatus.Ready)
			{
				try
				{
					plugin.proxy.Run(mainBot);
					mainBot.CommandManager.RegisterPlugin(plugin);
					plugin.status = PluginStatus.Active;
					return PluginResponse.Ok;
				}
				catch (Exception ex)
				{
					UnloadPlugin(plugin, false);
					Log.Write(Log.Level.Warning, "Plugin could not be loaded: ", ex);
					return PluginResponse.Crash;
				}
			}
			return PluginResponse.UnknownStatus;
		}

		private int GetFreeId()
		{
			int id = 0;
			while (usedIds.Contains(id))
				id++;
			usedIds.Add(id);
			return id;
		}

		public PluginResponse UnloadPlugin(string identifier)
		{
			int num;
			Plugin plugin;

			if (int.TryParse(identifier, out num))
			{
				plugin = plugins.Select(kvp => kvp.Value).FirstOrDefault(p => p.Id == num);
				return UnloadPlugin(plugin, true);
			}

			if (plugins.TryGetValue(identifier, out plugin))
				return UnloadPlugin(plugin, true);

			plugin = plugins.Select(kvp => kvp.Value).FirstOrDefault(p => p.proxy?.Name == identifier);
			return UnloadPlugin(plugin, true);
		}

		private PluginResponse UnloadPlugin(Plugin plugin, bool keepUnloaded)
		{
			if (plugin == null)
				return PluginResponse.PluginNotFound;

			plugin.Unload();

			if (keepUnloaded)
				plugin.status = PluginStatus.Disabled;
			return PluginResponse.Ok;
		}

		public string GetPluginOverview()
		{
			CheckAndClearPlugins();

			if (plugins.Count == 0)
			{
				return "No plugins found!";
			}
			else
			{
				var strb = new StringBuilder();
				strb.AppendLine("All available plugins:");
				int digits = (int)Math.Floor(Math.Log10(plugins.Count) + 1);
				foreach (var plugin in plugins.Values)
				{
					strb.Append("#").Append(plugin.Id.ToString("D" + digits)).Append('|');
					switch (plugin.status)
					{
					case PluginStatus.Off: strb.Append("OFF"); break;
					case PluginStatus.Ready: strb.Append("RDY"); break;
					case PluginStatus.Active: strb.Append("+ON"); break;
					case PluginStatus.Disabled: strb.Append("UNL"); break;
					default: throw new InvalidProgramException();
					}
					strb.Append('|').AppendLine(plugin.proxy?.Name ?? "<not loaded>");
				}
				return strb.ToString();
			}
		}

		public void Dispose()
		{
			foreach (var plugin in plugins.Values)
				UnloadPlugin(plugin, true);
		}
	}

	public interface ITS3ABPlugin : IDisposable
	{
		void Initialize(MainBot bot);
	}

	public class Plugin : MarshalByRefObject
	{
		private MainBot mainBot;
		public int Id;
		public FileInfo file;
		public PluginStatus status;

		public Plugin(FileInfo file, MainBot parent)
		{
			mainBot = parent;
			this.file = file;
			status = PluginStatus.Off;
		}

		public AppDomain domain;
		internal PluginProxy proxy;

		private static readonly FileInfo ts3File = new FileInfo(typeof(PluginProxy).Assembly.Location);
		private static readonly Type proxType = typeof(PluginProxy);

		public IEnumerable<BotCommand> GetWrappedCommands() => proxy.GetWrappedCommands();

		public PluginResponse Prepare()
		{
			try
			{
				PluginResponse result;
				if (file.Extension == ".cs")
					result = PrepareSource();
				else if (file.Extension == ".dll" || file.Extension == ".exe")
					result = PrepareBinary();
				else
					return PluginResponse.UnsupportedFile;

				if (result == PluginResponse.Ok)
					status = PluginStatus.Ready;
				return result;
			}
			catch (Exception ex)
			{
				Log.Write(Log.Level.Warning, "Possible plugin failed to load: ", ex);
				return PluginResponse.Crash;
			}
		}

		private PluginResponse PrepareBinary()
		{
			domain = AppDomain.CreateDomain(
				"Plugin_" + file.Name,
				AppDomain.CurrentDomain.Evidence,
				new AppDomainSetup
				{
					ApplicationBase = ts3File.Directory.FullName,
					PrivateBinPath = "Plugin/..;Plugin",
					PrivateBinPathProbe = ""
				});
			domain.UnhandledException += (s, e) => Unload();

			proxy = (PluginProxy)domain.CreateInstanceAndUnwrap(
				proxType.Assembly.FullName,
				proxType.FullName);
			proxy.ResolvePlugin = file;
			proxy.ResolveName = AssemblyName.GetAssemblyName(file.FullName);
			proxy.LoadAssembly(domain);

			return PluginResponse.Ok;
		}

		private PluginResponse PrepareSource()
		{
			return PluginResponse.UnsupportedFile;
		}

		public void Unload()
		{
			try
			{
				if (status == PluginStatus.Active)
					mainBot.CommandManager.UnregisterPlugin(this);

				if (proxy != null)
					proxy.Stop();

				if (domain != null)
					AppDomain.Unload(domain);
			}
			finally
			{
				proxy = null;
				domain = null;
				status = PluginStatus.Off;
			}
		}
	}

	class PluginProxy : MarshalByRefObject
	{
		public FileInfo ResolvePlugin;
		public AssemblyName ResolveName;

		private Type pluginType;
		private Assembly assembly;
		private ITS3ABPlugin pluginObject;
		private MethodInfo[] pluginMethods;

		private static readonly Type[] DynParam = new[] { typeof(object[]) };
		private static readonly Type[] InvokeParam = new[] { typeof(object), typeof(object[]) };
		private delegate void WrapperMethod(object obj, object[] param);

		public PluginProxy()
		{
			pluginObject = null;
		}

		public PluginResponse LoadAssembly(AppDomain domain)
		{
			try
			{
				assembly = domain.Load(ResolveName);

				var types = assembly.GetExportedTypes().Where(t => typeof(ITS3ABPlugin).IsAssignableFrom(t));

				if (!types.Any())
					return PluginResponse.NoTypeMatch;

				pluginType = types.First();
				return PluginResponse.Ok;
			}
			catch (Exception ex)
			{
				return PluginResponse.Crash;
			}
		}

		public void Run(MainBot bot)
		{
			pluginObject = (ITS3ABPlugin)Activator.CreateInstance(pluginType);
			pluginObject.Initialize(bot);
		}

		public void Stop()
		{
			if (pluginObject != null)
			{
				pluginObject.Dispose();
				pluginObject = null;
			}
		}

		public List<WrappedCommand> GetWrappedCommands()
		{
			var comBuilds = CommandManager.GetCommandMethods(pluginObject);

			var pluginMethodList = new List<MethodInfo>();
			var wrappedList = new List<WrappedCommand>();
			foreach (var comData in comBuilds)
			{
				pluginMethodList.Add(comData.method);
				int index = pluginMethodList.Count - 1;
				comData.usageList = comData.method.GetCustomAttributes<UsageAttribute>();
				wrappedList.Add(new WrappedCommand(index, this, comData));
			}
			pluginMethods = pluginMethodList.ToArray();

			return wrappedList;
		}

		private static Type CreateDelegateType(Type ret, Type[] param)
		{
			var tArgs = new List<Type>(param);
			tArgs.Add(ret);
			return Expression.GetDelegateType(tArgs.ToArray());
		}

		public object InvokeMethod(int num, object[] param)
		{
			return pluginMethods[num].Invoke(pluginObject, param);
		}

		public string Name => pluginType.Name;
	}

	class WrappedCommand : BotCommand
	{
		private PluginProxy proxy;
		private int mId;

		public WrappedCommand(int invNum, PluginProxy wrapParent, CommandBuildInfo data) : base(data)
		{
			proxy = wrapParent;
			mId = invNum;
		}

		protected override object ExecuteFunction(object[] parameters)
		{
			try
			{
				return proxy.InvokeMethod(mId, parameters);
			}
			catch (TargetInvocationException ex)
			{
				throw ex.InnerException;
			}
		}
	}

	public enum PluginStatus
	{
		/// <summary>The plugin has just been found and is ready to be prepared.</summary>
		Off,
		/// <summary>The plugin is valid and ready to be loaded.</summary>
		Ready,
		/// <summary>The plugin is currently active.</summary>
		Active,
		/// <summary>The plugin has been plugged off intentionally and will not be prepared with the next scan.</summary>
		Disabled,
	}

	public enum PluginResponse
	{
		Ok,
		UnsupportedFile,
		Crash,
		NoTypeMatch,
		UnknownStatus,
		PluginNotFound,
	}

	public struct PluginManagerData
	{
		[Info("The relative path to the pugins", "Plugins")]
		public string PluginPath;
	}
}
