﻿//
// ChildrenManager.cs:
//
// Author:
//   Leonardo Taglialegne <leonardo.taglialegne@gmail.com>
//
// Copyright (c) 2013 Leonardo Taglialegne.
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections.Generic;
using System.IO;
using Mono.Unix;
using Mono.WebServer.Log;
using System.Diagnostics;
using Mono.FastCgi;
using Mono.WebServer.FastCgi;
using System.Threading;

namespace Mono.WebServer.Fpm
{
	static class ChildrenManager
	{
		static readonly List<ChildInfo> children = new List<ChildInfo>();

		public static void KillChildren()
		{
			foreach (ChildInfo child in children) {
				try {
					Process process = child.Process;
					if (process != null && !process.HasExited)
						process.Kill();
				} catch (InvalidOperationException) {
					// Died between the if and the kill
				}
			}
			children.RemoveAll(child => child.Process != null && child.Process.HasExited);
		}

		public static void TermChildren()
		{
			foreach (ChildInfo child in children) {
				try {
					Process process = child.Process;
					if (process != null && !process.HasExited) {
						//TODO: Write some nice close code
					}
				} catch (InvalidOperationException) {
					// Died between the if and the kill
				}
			}
			children.RemoveAll (child => child.Process != null && child.Process.HasExited);
		}

		public static void StartChildren(FileInfo[] configFiles, ConfigurationManager configurationManager)
		{
			if (configFiles == null)
				throw new ArgumentNullException ("configFiles");
			if (configurationManager == null)
				throw new ArgumentNullException ("configurationManager");
			foreach (FileInfo fileInfo in configFiles) {
				if (fileInfo == null)
					continue;
				var childConfigurationManager = new ChildConfigurationManager ("child-" + fileInfo.Name);
				Logger.Write (LogLevel.Debug, "Loaded {0} [{1}]", fileInfo.Name, childConfigurationManager.InstanceType.ToString ().ToLowerInvariant ());
				string configFile = fileInfo.FullName;
				if (!childConfigurationManager.TryLoadXmlConfig (configFile))
					continue;
				string fastCgiCommand = configurationManager.FastCgiCommand;

				Func<Process> spawner;
				switch (childConfigurationManager.InstanceType) {
					case InstanceType.Ondemand:
						if (String.IsNullOrEmpty (childConfigurationManager.ShimSocket))
							throw new Exception ("You must specify a socket for the shim");
						spawner = () => Spawner.SpawnOndemandChild (childConfigurationManager.ShimSocket);
						break;
					default: // Static
						spawner = () => Spawner.SpawnStaticChild (configFile, fastCgiCommand);
						break;
				}

				Action spawnShim = () => Spawner.SpawnShim (configurationManager.ShimCommand, childConfigurationManager.ShimSocket, configFile, fastCgiCommand);

				string user = childConfigurationManager.User;
				if (String.IsNullOrEmpty (user)) {
					if (Platform.IsUnix) {
						Logger.Write (LogLevel.Warning, "Configuration file {0} didn't specify username, defaulting to file owner", fileInfo.Name);
						string owner = UnixFileSystemInfo.GetFileSystemEntry (configFile).OwnerUser.UserName;
						if (childConfigurationManager.InstanceType == InstanceType.Ondemand)
							Spawner.RunAs (owner, spawnShim) ();
						else
							spawner = Spawner.RunAs (owner, spawner);
					} else {
						Logger.Write (LogLevel.Warning, "Configuration file {0} didn't specify username, defaulting to the current one", fileInfo.Name);
						if (childConfigurationManager.InstanceType != InstanceType.Ondemand)
							spawnShim ();
					}
				} else {
					if (childConfigurationManager.InstanceType == InstanceType.Ondemand)
						Spawner.RunAs (user, spawnShim) ();
					else
						spawner = Spawner.RunAs (user, spawner);
				}

				var child = new ChildInfo { Spawner = spawner, ConfigurationManager = childConfigurationManager, Name = configFile };
				children.Add (child);

				if (childConfigurationManager.InstanceType == InstanceType.Static) {
					if (child.TrySpawn ()) {
						Logger.Write (LogLevel.Notice, "Started fastcgi daemon [static] with pid {0} and config file {1}", child.Process.Id, Path.GetFileName (configFile));
						Thread.Sleep (500);
						// TODO: improve this (it's used to wait for the child to be ready)
					} else
						Logger.Write (LogLevel.Error, "Couldn't start child with config file {0}", configFile);
					break;
				} else {
					Socket socket;
					if (FastCgi.Server.TryCreateSocket (childConfigurationManager, out socket)) {
						var server = new GenericServer<Connection> (socket, child);
						server.Start (configurationManager.Stoppable, (int)childConfigurationManager.Backlog);
					}
				}
			}
		}
	}
}