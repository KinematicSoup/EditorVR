﻿using ConditionalCompilation;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.EditorVR.Utilities;

namespace UnityEditor.Experimental.EditorVR.Tests
{
	[InitializeOnLoad]
	public class CompilationTest
	{
		[Test]
		public void CCUEnabled()
		{
			Assert.IsTrue(ConditionalCompilationUtility.enabled);
		}

		[Test]
		public void NoCCUDefines()
		{
			var defines = EditorUserBuildSettings.activeScriptCompilationDefines.ToList();
			defines = defines.Except(ConditionalCompilationUtility.defines).ToList();
			TestCompile(defines.ToArray());
		}

		[Test]
		public void NoEditorVR()
		{
			var defines = EditorUserBuildSettings.activeScriptCompilationDefines.ToList();
			defines.Remove("UNITY_EDITORVR");
			TestCompile(defines.ToArray());
		}

		static void TestCompile(string[] defines)
		{
			var outputFile = "Temp/CCUTest.dll";

			var references = new List<string>();
			U.Object.ForEachAssembly(assembly =>
			{
				// Ignore project assemblies because they will cause conflicts
				if (assembly.FullName.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase))
					return;

				// System.dll is included automatically and will cause conflicts if referenced explicitly
				if (assembly.FullName.StartsWith("System", StringComparison.OrdinalIgnoreCase))
					return;

				// This assembly causes a ReflectionTypeLoadException on compile
				if (assembly.FullName.StartsWith("ICSharpCode.NRefactory", StringComparison.OrdinalIgnoreCase))
					return;

				var codeBase = assembly.CodeBase;
				var uri = new UriBuilder(codeBase);
				var path = Uri.UnescapeDataString(uri.Path);

				references.Add(path);
			});

			var sources = Directory.GetFiles(Application.dataPath, "*.cs", SearchOption.AllDirectories);

			var output = EditorUtility.CompileCSharp(sources, references.ToArray(), defines, outputFile);
			foreach (var o in output)
			{
				var line = o.ToLower();
				Assert.IsFalse(line.Contains("exception") || line.Contains("error") || line.Contains("warning"), string.Join("\n", output));
			}
		}
	}
}
