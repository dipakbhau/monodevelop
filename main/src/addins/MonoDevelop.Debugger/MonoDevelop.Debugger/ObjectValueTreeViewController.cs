//
// ObjectValueTreeViewController.cs
//
// Author:
//       gregm <gregm@microsoft.com>
//
// Copyright (c) 2019 Microsoft
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
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Mono.Debugging.Client;
using MonoDevelop.Components;
using MonoDevelop.Core;
using MonoDevelop.Ide;
using MonoDevelop.Ide.CodeCompletion;
using MonoDevelop.Components.Commands;
using MonoDevelop.Ide.Commands;
using MonoDevelop.Ide.Editor.Extension;
using MonoDevelop.Ide.Fonts;

namespace MonoDevelop.Debugger
{
	/*
	 * Issues?
	 *
	 * - RemoveChildren did an unregister of events for child nodes that were removed, we might need to do the same for
	 * refreshing a node (which may replace it's children nodes)
	 *
	 */ 


	public class ObjectValueTreeViewController
	{
		public static int MaxEnumerableChildrenToFetch = 20;

		/// <summary>
		/// Holds a dictionary of tasks that are fetching children values of the given node
		/// </summary>
		readonly Dictionary<IObjectValueNode, Task<int>> childFetchTasks = new Dictionary<IObjectValueNode, Task<int>> ();

		// TODO: can we refactor this to a separate class?
		/// <summary>
		/// Holds a dictionary of arbitrary objects for nodes that are currently "Evaluating" by the debugger
		/// When the node has completed evaluation ValueUpdated event will be fired, passing the given object
		/// </summary>
		readonly Dictionary<IObjectValueNode, object> evaluationWatches = new Dictionary<IObjectValueNode, object> ();

		public ObjectValueTreeViewController ()
		{
		}

		public IDebuggerService Debugger { get; private set; }
		public IObjectValueNode Root { get; private set; }

		public IStackFrame Frame { get; set; }
		public bool AllowEditing { get; set; }
		public bool AllowAdding { get; set; }


		public bool CanQueryDebugger {
			get {
				EnsureDebuggerService ();
				return Debugger.IsConnected && Debugger.IsPaused;
			}
		}



		public event EventHandler<ChildrenChangedEventArgs> ChildrenLoaded;

		/// <summary>
		/// NodeExpanded is fired when the node has expanded and the children
		/// for the node have been loaded and are in the node's children collection
		/// </summary>
		public event EventHandler<NodeExpandedEventArgs> NodeExpanded;

		/// <summary>
		/// EvaluationCompleted is fired when the debugger informs us that a node that
		/// was IsEvaluating has finished evaluating and the values of the node can
		/// be displaved
		/// </summary>
		public event EventHandler<NodeEvaluationCompletedEventArgs> EvaluationCompleted;

		public object GetControl()
		{
			return new GtkObjectValueTreeView (this);
		}

		/// <summary>
		/// Clears the controller of nodes and resets the root to a new empty node
		/// </summary>
		public void ClearValues()
		{
			Root = OnCreateRoot ();
			OnChildrenLoaded (Root, 0, Root.Children.Count);
		}

		/// <summary>
		/// Adds values to the root node, eg locals or watch expressions
		/// </summary>
		public void AddValues(IEnumerable<IObjectValueNode> values)
		{
			if (Root == null) {
				Root = OnCreateRoot ();
			}

			var allNodes = values.ToList ();
			((RootObjectValueNode)Root).AddValues (allNodes);

			// TODO: we want to enumerate just the once
			foreach (var x in allNodes) {
				RegisterForEvaluationCompletion (x);
			}

			OnChildrenLoaded (Root, 0, Root.Children.Count);
		}

		public void ChangeCheckpoint ()
		{
		}


		public void ResetChangeTracking () { }

		/// <summary>
		/// Clear everything
		/// </summary>
		public void ClearAll ()
		{
			ClearEvaluationCompletionRegistrations ();
			ClearValues ();
		}

		#region Fetching and loading children
		/// <summary>
		/// Marks a node as expanded and fetches children for the node if they have not been already fetched
		/// </summary>
		public async Task ExpandNodeAsync(IObjectValueNode node, CancellationToken cancellationToken)
		{
			// if we think the node is expanded already, no need to trigger this again
			if (node.IsExpanded)
				return;

			node.IsExpanded = true;

			int loadedCount = 0;
			if (node.IsEnumerable) {
				// if we already have some loaded, don't load more - that is a specific user gesture
				if (node.Children.Count == 0) {
					// page the children in, instead of loading them all at once
					loadedCount = await FetchChildrenAsync (node, MaxEnumerableChildrenToFetch, cancellationToken);
				}
			} else {
				loadedCount = await FetchChildrenAsync (node, 0, cancellationToken);
			}

			if (loadedCount > 0) {
				OnChildrenLoaded (node, 0, node.Children.Count);
			}

			OnNodeExpanded (node);
		}

		/// <summary>
		/// Marks a node as not expanded
		/// </summary>
		public void CollapseNode(IObjectValueNode node)
		{
			node.IsExpanded = false;
		}

		public async Task<int> FetchMoreChildrenAsync (IObjectValueNode node, CancellationToken cancellationToken)
		{
			if (node.ChildrenLoaded) {
				return 0;
			}

			try {
				if (childFetchTasks.TryGetValue (node, out Task<int> task)) {
					// there is already a task to fetch the children
					return await task;
				} else {
					try {
						var oldCount = node.Children.Count;
						var result = await node.LoadChildrenAsync (MaxEnumerableChildrenToFetch, cancellationToken);

						// if any of them are still evaluating register for
						// a completion event so that we can tell the UI
						for (int i = oldCount; i < oldCount + result; i++) {
							var c = node.Children [i];
							RegisterForEvaluationCompletion (c);
						}

						// always send the event so that the UI can determine if the node has finished loading.
						OnChildrenLoaded (node, oldCount, result);

						return result;
					} finally {
						childFetchTasks.Remove (node);
					}
				}
			} catch (Exception ex) {
				// TODO: log or fail?
			}

			return 0;
		}

		/// <summary>
		/// Fetches the child nodes and returns the count of new children that were loaded.
		/// The children will be in node.Children.
		/// </summary>
		async Task<int> FetchChildrenAsync(IObjectValueNode node, int count, CancellationToken cancellationToken)
		{
			if (node.ChildrenLoaded) {
				return 0;
			}

			try {
				if (childFetchTasks.TryGetValue (node, out Task<int> task)) {
					// there is already a task to fetch the children
					return await task;
				} else {
					try {
						int result = 0;
						if (count > 0) {
							var oldCount = node.Children.Count;
							result = await node.LoadChildrenAsync (count, cancellationToken);

							// if any of them are still evaluating register for
							// a completion event so that we can tell the UI
							for (int i = oldCount; i < oldCount + result; i++) {
								var c = node.Children [i];
								RegisterForEvaluationCompletion (c);
							}
						} else {
							result = await node.LoadChildrenAsync (cancellationToken);

							// if any of them are still evaluating register for
							// a completion event so that we can tell the UI
							foreach (var c in node.Children) {
								RegisterForEvaluationCompletion (c);
							}
						}

						return result;
					} finally {
						childFetchTasks.Remove (node);
					}
				}
			} catch (Exception ex) {
				// TODO: log or fail?
			}

			return 0;
		}
		/*
		async Task LoadIEnumerableChildrenAsync (IObjectValueNode node, CancellationToken cancellationToken)
		{
			var value = GetDebuggerObjectValueAtIter (iter);
			if (enumerableLoading.Contains (value))
				return;
			enumerableLoading.Add (value);
			store.SetValue (iter, ValueButtonTextColumn, "");
			if (value.Name == "") {
				store.IterParent (out iter, iter);
				value = GetDebuggerObjectValueAtIter (iter);
			}

			int numberOfChildren = store.IterNChildren (iter);
			Task.Factory.StartNew<ObjectValue []> (delegate (object arg) {
				try {
					return ((ObjectValue)arg).GetRangeOfChildren (numberOfChildren - 1, 20);
				} catch (Exception ex) {
					// Note: this should only happen if someone breaks ObjectValue.GetAllChildren()
					LoggingService.LogError ("Failed to get ObjectValue children.", ex);
					return new ObjectValue [0];
				}
			}, value, cancellationTokenSource.Token).ContinueWith (t => {
				TreeIter it;
				if (disposed)
					return;
				store.IterNthChild (out it, iter, numberOfChildren - 1);
				foreach (var child in t.Result) {
					SetValues (iter, it, null, child);
					RegisterValue (child, it);
					it = store.InsertNodeAfter (it);
				}
				ScrollToCell (store.GetPath (it), expCol, true, 0f, 0f);
				if (t.Result.Length == 20) {//If we get back 20 elements it means there is probably more...
					SetValues (iter, it, null, ObjectValue.CreateNullObject (null, "", "", ObjectValueFlags.IEnumerable));
				} else {
					store.Remove (ref it);
				}

				if (compact)
					RecalculateWidth ();
				enumerableLoading.Remove (value);
			}, cancellationTokenSource.Token, TaskContinuationOptions.NotOnCanceled, Xwt.Application.UITaskScheduler);
		}
		*/
		#endregion

		#region Evaluation watches

		/// <summary>
		/// Registers the ValueChanged event for a node where IsEvaluating is true
		/// </summary>
		void RegisterForEvaluationCompletion (IObjectValueNode node)
		{
			if (node != null && node.IsEvaluating) {
				evaluationWatches [node] = null;
				node.ValueChanged += OnEvaluatingNodeValueChanged;
			}
		}

		/// <summary>
		/// Removes the ValueChanged handler from the node
		/// </summary>
		void UnregisterForEvaluationCompletion (IObjectValueNode node)
		{
			if (node != null) {
				node.ValueChanged -= OnEvaluatingNodeValueChanged;
				evaluationWatches.Remove (node);
			}
		}

		/// <summary>
		/// Removes all ValueChanged handlers for evaluating nodes
		/// </summary>
		void ClearEvaluationCompletionRegistrations()
		{
			foreach (var node in evaluationWatches.Keys) {
				node.ValueChanged -= OnEvaluatingNodeValueChanged;
			}

			evaluationWatches.Clear ();
		}

		#endregion


		/// <summary>
		/// Called when clearing, by default sets the root to a new ObjectValueNode
		/// </summary>
		protected virtual IObjectValueNode OnCreateRoot ()
		{
			return new RootObjectValueNode ();
		}

		protected virtual IDebuggerService OnGetDebuggerService()
		{
			return new ProxyDebuggerService ();
		}

		void EnsureDebuggerService()
		{
			if (Debugger == null) {
				Debugger = OnGetDebuggerService ();
			}
		}

		#region Event triggers
		void OnChildrenLoaded (IObjectValueNode node, int index, int count)
		{
			ChildrenLoaded?.Invoke (this, new ChildrenChangedEventArgs (node, index, count));
		}

		/// <summary>
		/// Triggered in response to ValueChanged on a node
		/// </summary>
		void OnEvaluatingNodeValueChanged (object sender, EventArgs e)
		{
			if (sender is IObjectValueNode node) {
				UnregisterForEvaluationCompletion (node);

				if (sender is IEvaluatingGroupObjectValueNode evalGroupNode) {
					if (evalGroupNode.IsEvaluatingGroup) {
						var replacementNodes = evalGroupNode.GetEvaluationGroupReplacementNodes();

						foreach (var newNode in replacementNodes) {
							RegisterForEvaluationCompletion (newNode);
						}

						OnEvaluationCompleted (sender as IObjectValueNode, replacementNodes);
					} else {
						OnEvaluationCompleted (sender as IObjectValueNode);
					}
				} else {
					OnEvaluationCompleted (sender as IObjectValueNode);
				}
			}
		}

		void OnEvaluationCompleted (IObjectValueNode node)
		{
			EvaluationCompleted?.Invoke (this, new NodeEvaluationCompletedEventArgs (node, new IObjectValueNode [1] { node }));
		}

		void OnEvaluationCompleted (IObjectValueNode node, IObjectValueNode[] replacementNodes)
		{
			EvaluationCompleted?.Invoke (this, new NodeEvaluationCompletedEventArgs (node, replacementNodes));
		}

		void OnNodeExpanded (IObjectValueNode node)
		{
			NodeExpanded?.Invoke (this, new NodeExpandedEventArgs (node));
		}
		#endregion
	}

	#region Extension methods and helpers
	/// <summary>
	/// Helper class to mimic existing API
	/// </summary>
	public static class ObjectValueTreeViewControllerExtensions
	{
		public static void SetStackFrame(this ObjectValueTreeViewController controller, StackFrame frame)
		{
			controller.Frame = new ProxyStackFrame (frame);
		}

		public static StackFrame GetStackFrame (this ObjectValueTreeViewController controller)
		{
			return (controller.Frame as ProxyStackFrame)?.StackFrame;
		}


		public static void AddValues (this ObjectValueTreeViewController controller, IEnumerable<ObjectValue> values)
		{
			controller.AddValues (values.Select (x => new ObjectValueNode (x, controller.Root.Path)));
		}

	}

	public static class ObjectValueNodeExtensions
	{
		public static string GetDisplayValue(this IObjectValueNode node)
		{
			if (node.DisplayValue == null)
				return "(null)";

			if (node.DisplayValue.Length > 1000)
				// Truncate the string to stop the UI from hanging
				// when calculating the size for very large amounts
				// of text.
				return node.DisplayValue.Substring (0, 1000) + "…";

			return node.DisplayValue;
		}

		public static ObjectValue GetDebuggerObjectValue(this IObjectValueNode node)
		{
			if (node is ObjectValueNode val) {
				return val.DebuggerObject;
			}

			return null;
		}

		public static bool GetIsEvaluatingGroup (this IObjectValueNode node)
		{
			return (node is IEvaluatingGroupObjectValueNode evg && evg.IsEvaluatingGroup);
		}
	}
	#endregion
}
