// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

namespace TS3AudioBot.Dependency
{
	using Helper;
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Linq;
	using System.Reflection;

	public class Injector : DependencyRealm, ICoreModule
	{
		public Injector()
		{
		}

		public void Initialize() { }

		public R<T> GetCoreModule<T>() where T : ICoreModule
		{
			if(loaded.TryGetValue(typeof(T), out var mod))
				return (T)mod.Obj;
			return "Module not found";
		}
	}

	public class Module
	{
		private static readonly ConcurrentDictionary<Type, Type[]> typeData;

		public bool IsInitialized { get; set; }
		public object Obj { get; }
		public Type BaseType { get; }
		// object SyncContext;

		static Module()
		{
			Util.Init(out typeData);
		}

		public Module(object obj, Type baseType)
		{
			IsInitialized = false;
			Obj = obj;
			BaseType = baseType;
		}

		public Type[] GetDependants() => GetDependants(Obj.GetType());

		public IEnumerable<PropertyInfo> GetModuleProperties() => GetModuleProperties(Obj.GetType());

		private static Type[] GetDependants(Type type)
		{
			if (!typeData.TryGetValue(type, out var depArr))
			{
				depArr = GetModuleProperties(type).Select(p => p.PropertyType).ToArray();
				typeData[type] = depArr;
			}
			return depArr;
		}

		private static IEnumerable<PropertyInfo> GetModuleProperties(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
			.Where(p => p.CanRead && p.CanWrite && typeof(ITabModule).IsAssignableFrom(p.PropertyType));
	}

	public class DependencyRealm
	{
		protected ConcurrentDictionary<Type, Module> loaded;
		protected List<Module> waiting;

		public DependencyRealm()
		{
			Util.Init(out loaded);
			Util.Init(out waiting);
		}

		public T Create<T>() where T : ICoreModule => (T)CreateFromType(typeof(T));
		public object CreateFromType(Type type)
		{
			var obj = Activator.CreateInstance(type);
			RegisterInjectable(obj, false);
			return obj;
		}
		public void RegisterModule<T>(T obj, bool initialized = false) where T : ICoreModule => RegisterInjectable(obj, initialized, typeof(T));
		public void RegisterInjectable(object obj, bool initialized = false, Type baseType = null)
		{
			var modType = baseType ?? obj.GetType();
			var mod = new Module(obj, modType) { IsInitialized = initialized };
			if (initialized)
				SetInitalized(mod);
			else
				waiting.Add(mod);
			DoQueueInitialize();
		}

		public void SkipInitialized(object obj)
		{
			var (mod, idx) = waiting.Select((m, i) => (mod: m, idx: i)).FirstOrDefault(t => t.mod.Obj == obj);
			if (mod == null)
				return;

			waiting.RemoveAt(idx);
			SetInitalized(mod);

			DoQueueInitialize();
		}

		public void ForceCyclicResolve()
		{

		}

		protected bool SetInitalized(Module module)
		{
			if (!module.IsInitialized && module.Obj is ITabModule tabModule)
			{
				tabModule.Initialize();
			}
			module.IsInitialized = true;
			loaded[module.BaseType] = module;
			return true;
		}

		protected void DoQueueInitialize()
		{
			bool changed;
			do
			{
				changed = false;
				for (int i = 0; i < waiting.Count; i++)
				{
					var mod = waiting[i];
					if (IsResolvable(mod))
					{
						if (!DoResolve(mod))
						{
							// TODO warn
							continue;
						}
						changed = true;
						waiting.RemoveAt(i);
						i--;
					}
				}
			} while (changed);
		}

		protected bool IsResolvable(Module module)
		{
			var deps = module.GetDependants();
			foreach (var depeningType in deps)
			{
				if (!loaded.ContainsKey(depeningType)) // todo maybe to some linear inheritance checking
					return false;
			}
			return true;
		}

		protected bool DoResolve(Module module)
		{
			var props = module.GetModuleProperties();
			foreach (var prop in props)
			{
				if (loaded.TryGetValue(prop.PropertyType, out var depModule))
				{
					prop.SetValue(module.Obj, depModule.Obj);
				}
				else
				{
					return false;
				}
			}
			return SetInitalized(module);
		}

		public bool AllResolved() => waiting.Count == 0;

		public void Unregister(Type type)
		{
			throw new NotImplementedException();
		}
	}
}
