﻿//
// MSBuildProjectInstance.cs
//
// Author:
//       Lluis Sanchez Gual <lluis@xamarin.com>
//
// Copyright (c) 2015 Xamarin, Inc (http://www.xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using System.Collections.Generic;
using System.Xml;
using MonoDevelop.Core;
using System.Linq;

namespace MonoDevelop.Projects.Formats.MSBuild
{
	public class MSBuildProjectInstance
	{
		MSBuildProject msproject;
		List<IMSBuildItemEvaluated> evaluatedItems = new List<IMSBuildItemEvaluated> ();
		List<IMSBuildItemEvaluated> evaluatedItemsIgnoringCondition = new List<IMSBuildItemEvaluated> ();
		MSBuildEvaluatedPropertyCollection evaluatedProperties;
		MSBuildTarget[] targets = new MSBuildTarget[0];
		Dictionary<string,string> globalProperties = new Dictionary<string, string> ();

		MSBuildProjectInstanceInfo info;

		public MSBuildProjectInstance (MSBuildProject project)
		{
			msproject = project;
			evaluatedItemsIgnoringCondition = new List<IMSBuildItemEvaluated> ();
			evaluatedProperties = new MSBuildEvaluatedPropertyCollection (msproject);
		}

		public void SetGlobalProperty (string property, string value)
		{
			globalProperties [property] = value;
		}

		internal bool OnlyEvaluateProperties { get; set; }

		public void Evaluate ()
		{
			info = msproject.LoadNativeInstance ();
			var e = info.Engine;
			var pi = e.CreateProjectInstance (info.Project);

			try {
				foreach (var prop in globalProperties)
					e.SetGlobalProperty (pi, prop.Key, prop.Value);

				e.Evaluate (pi);

				SyncBuildProject (info.ItemMap, info.Engine, pi);
			}
			catch (Exception ex) {
				// If the project can't be evaluated don't crash
				LoggingService.LogError ("MSBuild project could not be evaluated", ex);
				throw new ProjectEvaluationException (msproject, ex.Message);
			}
		}

		internal const string NodeIdPropertyName = "__MD_NodeId";

		void SyncBuildProject (Dictionary<string,MSBuildItem> currentItems, MSBuildEngine e, object project)
		{
			DateTime t = DateTime.Now;
			evaluatedItemsIgnoringCondition.Clear ();
			evaluatedItems.Clear ();

			if (!OnlyEvaluateProperties) {
				
/*				foreach (var it in e.GetAllItems (project, false)) {
					string name, include, finalItemSpec;
					bool imported;
					e.GetItemInfo (it, out name, out include, out finalItemSpec, out imported);
					var iid = e.GetItemMetadata (it, NodeIdPropertyName);
					MSBuildItem xit;
					if (currentItems.TryGetValue (iid, out xit)) {
						xit.SetEvalResult (finalItemSpec);
						((MSBuildPropertyGroupEvaluated)xit.EvaluatedMetadata).Sync (e, it);
					}
				}*/

				Console.WriteLine ("t1:" + (DateTime.Now - t).TotalMilliseconds);
				t = DateTime.Now;

				var xmlImports = msproject.Imports.ToArray ();
				var buildImports = e.GetImports (project).ToArray ();
				for (int n = 0; n < xmlImports.Length && n < buildImports.Length; n++)
					xmlImports [n].SetEvalResult (e.GetImportEvaluatedProjectPath (project, buildImports [n]));

				Console.WriteLine ("t2:" + (DateTime.Now - t).TotalMilliseconds);
				t = DateTime.Now;

				var evalItems = new Dictionary<string,MSBuildItemEvaluated> ();
				foreach (var it in e.GetEvaluatedItems (project)) {
					var xit = it as MSBuildItemEvaluated;
					if (xit == null) {
						xit = CreateEvaluatedItem (e, it);
						var itemId = e.GetItemMetadata (it, NodeIdPropertyName);
						var key = itemId + " " + xit.Include;
						if (evalItems.ContainsKey (key))
							continue; // xbuild seems to return duplicate items when using wildcards. This is a workaround to avoid the duplicates.
						MSBuildItem pit;
						if (!string.IsNullOrEmpty (itemId) && currentItems.TryGetValue (itemId, out pit)) {
							xit.SourceItem = pit;
							xit.Condition = pit.Condition;
							evalItems [key] = xit;
						}
					}
					evaluatedItems.Add (xit);
				}

				Console.WriteLine ("t3:" + (DateTime.Now - t).TotalMilliseconds);
				t = DateTime.Now;

				var evalItemsNoCond = new Dictionary<string,MSBuildItemEvaluated> ();
				foreach (var it in e.GetEvaluatedItemsIgnoringCondition (project)) {
					var xit = it as MSBuildItemEvaluated;
					if (xit == null) {
						xit = CreateEvaluatedItem (e, it);
						var itemId = e.GetItemMetadata (it, NodeIdPropertyName);
						MSBuildItemEvaluated evItem;
						var key = itemId + " " + xit.Include;
						if (evalItemsNoCond.ContainsKey (key))
							continue; // xbuild seems to return duplicate items when using wildcards. This is a workaround to avoid the duplicates.
						if (!string.IsNullOrEmpty (itemId) && evalItems.TryGetValue (key, out evItem)) {
							evaluatedItemsIgnoringCondition.Add (evItem);
							evalItemsNoCond [key] = evItem;
							continue;
						}
						MSBuildItem pit;
						if (!string.IsNullOrEmpty (itemId) && currentItems.TryGetValue (itemId, out pit)) {
							xit.SourceItem = pit;
							xit.Condition = pit.Condition;
							evalItemsNoCond [key] = xit;
						}
					}
					evaluatedItemsIgnoringCondition.Add (xit);
				}

				// Clear the node id metadata
				foreach (var it in evaluatedItems.Concat (evaluatedItemsIgnoringCondition))
					((MSBuildPropertyGroupEvaluated)it.Metadata).RemoveProperty (NodeIdPropertyName);

				Console.WriteLine ("t4:" + (DateTime.Now - t).TotalMilliseconds);
				t = DateTime.Now;

				targets = e.GetTargets (project).ToArray ();

				Console.WriteLine ("t5:" + (DateTime.Now - t).TotalMilliseconds);
				t = DateTime.Now;
			}

			var props = new MSBuildEvaluatedPropertyCollection (msproject);
			evaluatedProperties = props;
			props.SyncCollection (e, project);

			Console.WriteLine ("t6:" + (DateTime.Now - t).TotalMilliseconds);
		}

		MSBuildItemEvaluated CreateEvaluatedItem (MSBuildEngine e, object it)
		{
			string name, include, finalItemSpec;
			bool imported;
			e.GetEvaluatedItemInfo (it, out name, out include, out finalItemSpec, out imported);
			var xit = new MSBuildItemEvaluated (msproject, name, include, finalItemSpec);
			xit.IsImported = imported;
			((MSBuildPropertyGroupEvaluated)xit.Metadata).Sync (e, it);
			return xit;
		}

		public IMSBuildEvaluatedPropertyCollection EvaluatedProperties {
			get { return evaluatedProperties; }
		}

		public IEnumerable<IMSBuildItemEvaluated> EvaluatedItems {
			get { return evaluatedItems; }
		}

		public IEnumerable<IMSBuildItemEvaluated> EvaluatedItemsIgnoringCondition {
			get { return evaluatedItemsIgnoringCondition; }
		}

		public IEnumerable<MSBuildTarget> Targets {
			get {
				return targets;
			}
		}
	}

	class MSBuildProjectInstanceInfo
	{
		public object Project { get; set; }
		public MSBuildEngine Engine { get; set; }
		public int ProjectStamp { get; set; }
		public Dictionary<string,MSBuildItem> ItemMap { get; set; }
	}
}

