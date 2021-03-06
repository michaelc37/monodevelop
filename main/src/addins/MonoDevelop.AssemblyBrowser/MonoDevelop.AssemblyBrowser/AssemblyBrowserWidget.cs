//
// AssemblyBrowserWidget.cs
//
// Author:
//   Mike Krüger <mkrueger@novell.com>
//
// Copyright (C) 2008 Novell, Inc (http://www.novell.com)
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
using System.ComponentModel;
using System.Text;
using System.Xml;
using Gtk;

using Mono.Cecil;
using MonoDevelop.Components.Commands;
using MonoDevelop.Core;
using MonoDevelop.Ide;
using MonoDevelop.Ide.Commands;
using MonoDevelop.Ide.Gui.Components;
using System.Linq;
using MonoDevelop.Ide.TypeSystem;
using ICSharpCode.NRefactory.Documentation;
using ICSharpCode.NRefactory.TypeSystem;
using MonoDevelop.Projects;
using ICSharpCode.NRefactory.TypeSystem.Implementation;
using XmlDocIdLib;
using MonoDevelop.Ide.Gui;
using MonoDevelop.Components;
using System.Threading.Tasks;
using System.Threading;
using MonoDevelop.Ide.Editor;
using MonoDevelop.Ide.Navigation;
using MonoDevelop.Ide.Gui.Content;
using System.IO;

namespace MonoDevelop.AssemblyBrowser
{
	enum SearchMemberState {
		TypesAndMembers,
		Types,
		Members
	}
	[System.ComponentModel.Category("MonoDevelop.AssemblyBrowser")]
	[System.ComponentModel.ToolboxItem(true)]
	partial class AssemblyBrowserWidget : Gtk.Bin
	{
		Gtk.ComboBox comboboxVisibilty;
		MonoDevelop.Components.SearchEntry searchentry1;
		Gtk.ComboBox languageCombobox;

		public AssemblyBrowserTreeView TreeView {
			get;
			private set;
		}
		
		public bool PublicApiOnly {
			get {
				return TreeView.PublicApiOnly;
			}
			set {
				comboboxVisibilty.Active = value ? 0 : 1;
			}
		}
		
		DocumentationPanel documentationPanel = new DocumentationPanel ();
		readonly TextEditor inspectEditor;

		public class AssemblyBrowserTreeView : ExtensibleTreeView
		{
			bool publicApiOnly = true;

			public bool PublicApiOnly {
				get {
					return publicApiOnly;
				}
				set {
					if (publicApiOnly == value)
						return;
					publicApiOnly = value;
					var root = GetRootNode ();
					if (root != null) {
						do {
							RefreshNode (root);
						} while (root.MoveNext ());
					}
				}
			}

			public AssemblyBrowserTreeView (NodeBuilder[] builders, TreePadOption[] options) : base (builders, options)
			{
			}
		}

		static string GetLink (ReferenceSegment referencedSegment, out bool? isNotPublic)
		{
			isNotPublic = null;
			if (referencedSegment == null)
				return null;

			var td = referencedSegment.Reference as TypeDefinition;
			if (td != null) {
				isNotPublic = !td.IsPublic;
				return new XmlDocIdGenerator ().GetXmlDocPath ((TypeDefinition)referencedSegment.Reference);
			}
			var md = referencedSegment.Reference as MethodDefinition;
			if (md != null) {
				isNotPublic = !md.IsPublic;
				return new XmlDocIdGenerator ().GetXmlDocPath ((MethodDefinition)referencedSegment.Reference);
			}

			var pd = referencedSegment.Reference as PropertyDefinition;
			if (pd != null) {
				isNotPublic = (pd.GetMethod == null || !pd.GetMethod.IsPublic) &&  
					(pd.SetMethod == null || !pd.SetMethod.IsPublic);
				return new XmlDocIdGenerator ().GetXmlDocPath ((PropertyDefinition)referencedSegment.Reference);
			}

			var fd = referencedSegment.Reference as FieldDefinition;
			if (fd != null) {
				isNotPublic = !fd.IsPublic;
				return new XmlDocIdGenerator ().GetXmlDocPath ((FieldDefinition)referencedSegment.Reference);
			}

			var ed = referencedSegment.Reference as EventDefinition;
			if (ed != null) {
				return new XmlDocIdGenerator ().GetXmlDocPath ((EventDefinition)referencedSegment.Reference);
			}

			var tref = referencedSegment.Reference as MemberReference;
			if (tref != null) {
				return new XmlDocIdGenerator ().GetXmlDocPath (tref);
			}

			return referencedSegment.Reference.ToString ();
		}

		class FastNonInterningProvider : InterningProvider
		{
			Dictionary<string, string> stringDict = new Dictionary<string, string>();

			public override string Intern (string text)
			{
				if (text == null)
					return null;

				string output;
				if (stringDict.TryGetValue(text, out output))
					return output;
				stringDict [text] = text;
				return text;
			}

			public override ISupportsInterning Intern (ISupportsInterning obj)
			{
				return obj;
			}

			public override IList<T> InternList<T>(IList<T> list)
			{
				return list;
			}

			public override object InternValue (object obj)
			{
				return obj;
			}
		}

		public AssemblyBrowserWidget ()
		{
			this.Build ();

			comboboxVisibilty = ComboBox.NewText ();
			comboboxVisibilty.InsertText (0, GettextCatalog.GetString ("Only public members"));
			comboboxVisibilty.InsertText (1, GettextCatalog.GetString ("All members"));
			comboboxVisibilty.Active = Math.Min (1, Math.Max (0, PropertyService.Get ("AssemblyBrowser.MemberSelection", 0)));
			comboboxVisibilty.Changed += delegate {
				TreeView.PublicApiOnly = comboboxVisibilty.Active == 0;
				PropertyService.Set ("AssemblyBrowser.MemberSelection", comboboxVisibilty.Active);
				FillInspectLabel (); 
			};

			searchentry1 = new MonoDevelop.Components.SearchEntry ();
			searchentry1.Ready = true;
			searchentry1.HasFrame = true;
			searchentry1.WidthRequest = 200;
			searchentry1.Visible = true;
			UpdateSearchEntryMessage ();
			searchentry1.InnerEntry.Changed += SearchEntryhandleChanged;

			CheckMenuItem checkMenuItem = this.searchentry1.AddFilterOption (0, GettextCatalog.GetString ("Types and Members"));
			checkMenuItem.Active = PropertyService.Get ("AssemblyBrowser.SearchMemberState", SearchMemberState.TypesAndMembers) == SearchMemberState.TypesAndMembers;
			checkMenuItem.Toggled += delegate {
				if (checkMenuItem.Active) {
					searchMode = AssemblyBrowserWidget.SearchMode.TypeAndMembers;
					CreateColumns ();
					StartSearch ();
				}
				if (checkMenuItem.Active)
					PropertyService.Set ("AssemblyBrowser.SearchMemberState", SearchMemberState.TypesAndMembers);
				UpdateSearchEntryMessage ();
			};

			CheckMenuItem checkMenuItem2 = this.searchentry1.AddFilterOption (0, GettextCatalog.GetString ("Types"));
			checkMenuItem2.Active = PropertyService.Get ("AssemblyBrowser.SearchMemberState", SearchMemberState.TypesAndMembers) == SearchMemberState.Types;
			checkMenuItem2.Toggled += delegate {
				if (checkMenuItem2.Active) {
					searchMode = AssemblyBrowserWidget.SearchMode.Type;
					CreateColumns ();
					StartSearch ();
				}
				if (checkMenuItem.Active)
					PropertyService.Set ("AssemblyBrowser.SearchMemberState", SearchMemberState.Types);
				UpdateSearchEntryMessage ();
			};
			
			CheckMenuItem checkMenuItem1 = this.searchentry1.AddFilterOption (1, GettextCatalog.GetString ("Members"));
			checkMenuItem.Active = PropertyService.Get ("AssemblyBrowser.SearchMemberState", SearchMemberState.TypesAndMembers) == SearchMemberState.Members;
			checkMenuItem1.Toggled += delegate {
				if (checkMenuItem1.Active) {
					searchMode = AssemblyBrowserWidget.SearchMode.Member;
					CreateColumns ();
					StartSearch ();
				}
				if (checkMenuItem.Active)
					PropertyService.Set ("AssemblyBrowser.SearchMemberState", SearchMemberState.Members);
				UpdateSearchEntryMessage ();

			};

			languageCombobox = Gtk.ComboBox.NewText ();
			languageCombobox.AppendText (GettextCatalog.GetString ("Summary"));
			languageCombobox.AppendText (GettextCatalog.GetString ("IL"));
			languageCombobox.AppendText (GettextCatalog.GetString ("C#"));
			languageCombobox.Active = Math.Min (2, Math.Max (0, PropertyService.Get ("AssemblyBrowser.Language", 0)));
			languageCombobox.Changed += LanguageComboboxhandleChanged;

			loader = new CecilLoader (true);
			loader.InterningProvider = new FastNonInterningProvider ();
			loader.IncludeInternalMembers = true;
			TreeView = new AssemblyBrowserTreeView (new NodeBuilder[] { 
				new ErrorNodeBuilder (),
				new ProjectNodeBuilder (this),
				new AssemblyNodeBuilder (this),
				new ModuleReferenceNodeBuilder (),
				new AssemblyReferenceNodeBuilder (this),
				//new AssemblyReferenceFolderNodeBuilder (this),
				new AssemblyResourceFolderNodeBuilder (),
				new ResourceNodeBuilder (),
				new NamespaceBuilder (this),
				new DomTypeNodeBuilder (this),
				new DomMethodNodeBuilder (this),
				new DomFieldNodeBuilder (this),
				new DomEventNodeBuilder (this),
				new DomPropertyNodeBuilder (this),
				new BaseTypeFolderNodeBuilder (this),
				new BaseTypeNodeBuilder (this)
				}, new TreePadOption [0]);
			TreeView.AllowsMultipleSelection = false;
			TreeView.SelectionChanged += HandleCursorChanged;

			treeViewPlaceholder.Add (TreeView);

//			this.descriptionLabel.ModifyFont (Pango.FontDescription.FromString ("Sans 9"));
//			this.documentationLabel.ModifyFont (Pango.FontDescription.FromString ("Sans 12"));
//			this.documentationLabel.ModifyBg (Gtk.StateType.Normal, new Gdk.Color (255, 255, 225));
//			this.documentationLabel.Wrap = true;
			

			inspectEditor = TextEditorFactory.CreateNewEditor ();
			inspectEditor.Options = DefaultSourceEditorOptions.PlainEditor;

			//inspectEditor.ButtonPressEvent += HandleInspectEditorButtonPressEvent;
			
			this.inspectEditor.IsReadOnly = true;
//			this.inspectEditor.Document.SyntaxMode = new Mono.TextEditor.Highlighting.MarkupSyntaxMode ();
//			this.inspectEditor.LinkRequest += InspectEditorhandleLinkRequest;

			documentationScrolledWindow.PackStart (inspectEditor, true, true, 0);

			this.hpaned1.ExposeEvent += HPaneExpose;
			hpaned1 = hpaned1.ReplaceWithWidget (new HPanedThin (), true);
			hpaned1.Position = 271;

			this.notebook1.SetTabLabel (this.documentationScrolledWindow, new Label (GettextCatalog.GetString ("Documentation")));
			this.notebook1.SetTabLabel (this.searchWidget, new Label (GettextCatalog.GetString ("Search")));
			notebook1.Page = 0;
			//this.searchWidget.Visible = false;
				
			typeListStore = new Gtk.ListStore (typeof(Xwt.Drawing.Image), // type image
			                                   typeof(string), // name
			                                   typeof(string), // namespace
			                                   typeof(string), // assembly
				                               typeof(IMember)
			                                  );
			
			memberListStore = new Gtk.ListStore (typeof(Xwt.Drawing.Image), // member image
			                                   typeof(string), // name
			                                   typeof(string), // Declaring type full name
			                                   typeof(string), // assembly
				                               typeof(IMember)
			                                  );
			CreateColumns ();
//			this.searchEntry.Changed += SearchEntryhandleChanged;
			this.searchTreeview.RowActivated += SearchTreeviewhandleRowActivated;
			this.notebook1.ShowTabs = false;
			this.ShowAll ();
		}

		void UpdateSearchEntryMessage ()
		{
			switch (PropertyService.Get ("AssemblyBrowser.SearchMemberState", SearchMemberState.TypesAndMembers)) {
			case SearchMemberState.TypesAndMembers:
				searchentry1.EmptyMessage = GettextCatalog.GetString ("Search for types and members");
				break;
			case SearchMemberState.Types:
				searchentry1.EmptyMessage = GettextCatalog.GetString ("Search for types");
				break;
			case SearchMemberState.Members:
				searchentry1.EmptyMessage = GettextCatalog.GetString ("Search for members");
				break;
			default:
				throw new ArgumentOutOfRangeException ();
			}

		}

		internal void SetToolbar (DocumentToolbar toolbar)
		{
			Gtk.Label la = new Label (GettextCatalog.GetString ("Visibility"));
			toolbar.Add (la);

			toolbar.Add (comboboxVisibilty);

			la = new Label ("");
			toolbar.Add (la, true);

			toolbar.Add (searchentry1);

			la = new Label (GettextCatalog.GetString ("Language"));
			toolbar.Add (la);

			toolbar.Add (languageCombobox);

			toolbar.ShowAll ();
		}
		
		[CommandHandler (EditCommands.Copy)]
		protected void OnCopyCommand ()
		{
			EditActions.ClipboardCopy (inspectEditor);
		}
		
		[CommandHandler (EditCommands.SelectAll)]
		protected void OnSelectAllCommand ()
		{
			EditActions.SelectAll (inspectEditor);
		}
		
		void HandleInspectEditorButtonPressEvent (object o, ButtonPressEventArgs args)
		{
			if (args.Event.Button != 3)
				return;
			var menuSet = new CommandEntrySet ();
			menuSet.AddItem (EditCommands.SelectAll);
			menuSet.AddItem (EditCommands.Copy);
			IdeApp.CommandService.ShowContextMenu (this, args.Event, menuSet, this);
		}

		void SearchTreeviewhandleRowActivated (object o, RowActivatedArgs args)
		{
			TreeIter selectedIter;
			if (searchTreeview.Selection.GetSelected (out selectedIter)) {
				var member = (IUnresolvedEntity)(searchMode == SearchMode.Member ? memberListStore.GetValue (selectedIter, 4) : typeListStore.GetValue (selectedIter, 4));

				var nav = SearchMember (member);
				if (nav != null) {
					notebook1.Page = 0;
				}
			}
		}

		void SearchEntryhandleChanged (object sender, EventArgs e)
		{
			StartSearch ();
		}
		
		void LanguageComboboxhandleChanged (object sender, EventArgs e)
		{
			this.notebook1.Page = 0;
			PropertyService.Set ("AssemblyBrowser.Language", this.languageCombobox.Active);
			FillInspectLabel ();
		}


		public IEntity ActiveMember  {
			get;
			set;
		}
		
		protected override void OnRealized ()
		{
			base.OnRealized ();
			TreeView.GrabFocus ();
		}
		
		ITreeNavigator SearchMember (IUnresolvedEntity member, bool expandNode = true)
		{
			return SearchMember (GetIdString (member), expandNode);
		}
			
		ITreeNavigator SearchMember (string helpUrl, bool expandNode = true)
		{
			var nav = SearchMember (TreeView.GetRootNode (), helpUrl, expandNode);
			if (nav != null)
				return nav;
			// Constructor may be a generated default without implementation.
			var ctorIdx = helpUrl.IndexOf (".#ctor", StringComparison.Ordinal);
			if (helpUrl.StartsWith ("M:", StringComparison.Ordinal) && ctorIdx > 0) {
				return SearchMember ("T" + helpUrl.Substring (1, ctorIdx - 1), expandNode);
			}
			return null;
		}
		
		static void AppendTypeReference (StringBuilder result, ITypeReference type)
		{
			if (type is ByReferenceTypeReference) {
				var brtr = (ByReferenceTypeReference)type;
				AppendTypeReference (result, brtr.ElementType);
				return;
			}

			if (type is ArrayTypeReference) {
				var array = (ArrayTypeReference)type;
				AppendTypeReference (result, array.ElementType);
				result.Append ("[");
				result.Append (new string (',', array.Dimensions - 1));
				result.Append ("]");
				return;
			}

			if (type is PointerTypeReference) {
				var ptr = (PointerTypeReference)type;
				AppendTypeReference (result, ptr.ElementType);
				result.Append ("*");
				return;
			}

			if (type is GetClassTypeReference) {
				var r = (GetClassTypeReference)type;
				var n = r.FullTypeName.TopLevelTypeName;
				result.Append (n.Namespace + "." + n.Name);
				return;
			}

			if (type is IUnresolvedTypeDefinition) {
				result.Append (((IUnresolvedTypeDefinition)type).FullName);
			}

			if (type is TypeParameterReference) {
				result.Append ("`" +((TypeParameterReference)type).Index);
			}
		}
		
		static void AppendHelpParameterList (StringBuilder result, IList<IUnresolvedParameter> parameters)
		{
			if (parameters == null || parameters.Count == 0)
				return;
			result.Append ('(');
			if (parameters != null) {
				for (int i = 0; i < parameters.Count; i++) {
					if (i > 0)
						result.Append (',');
					var p = parameters [i];
					if (p == null)
						continue;
					AppendTypeReference (result, p.Type);
					if (p.IsRef)
						result.Append ("&");
					if (p.IsOut) {
						result.Append ("@");
					}
				}
			}
			result.Append (')');
		}
		
		internal static string GetIdString (IUnresolvedEntity member)
		{
			StringBuilder sb;
			
			switch (member.SymbolKind) {
			case ICSharpCode.NRefactory.TypeSystem.SymbolKind.TypeDefinition:
				var type = member as IUnresolvedTypeDefinition;
				if (type.TypeParameters.Count == 0)
					return "T:" + type.FullName;
				return "T:" + type.FullName + "`" + type.TypeParameters.Count;
			case SymbolKind.Method:
				var method = (IUnresolvedMethod)member;
				sb = new StringBuilder ();
				sb.Append ("M:");
				sb.Append (method.DeclaringTypeDefinition.ReflectionName);
				sb.Append (".");
				sb.Append (method.Name);
				if (method.TypeParameters.Count > 0) {
					sb.Append ("`");
					sb.Append (method.TypeParameters.Count);
				}
				AppendHelpParameterList (sb, method.Parameters);
				return sb.ToString ();
			case SymbolKind.Constructor:
				var constructor = (IUnresolvedMethod)member;
				sb = new StringBuilder ();
				sb.Append ("M:");
				sb.Append (constructor.DeclaringTypeDefinition.FullName);
				sb.Append (".#ctor");
				AppendHelpParameterList (sb, constructor.Parameters);
				return sb.ToString ();
			case SymbolKind.Destructor: // todo
				return "todo";
			case SymbolKind.Property:
				sb = new StringBuilder ();
				sb.Append ("P:");
				sb.Append (member.DeclaringTypeDefinition.ReflectionName);
				sb.Append (".");
				sb.Append (member.Name);
				return sb.ToString ();
			case SymbolKind.Indexer:
				var indexer = (IUnresolvedProperty)member;
				sb = new StringBuilder ();
				sb.Append ("P:");
				sb.Append (indexer.DeclaringTypeDefinition.ReflectionName);
				sb.Append (".Item");
				AppendHelpParameterList (sb, indexer.Parameters);
				return sb.ToString ();
			case SymbolKind.Field:
				sb = new StringBuilder ();
				sb.Append ("F:");
				sb.Append (member.DeclaringTypeDefinition.ReflectionName);
				sb.Append (".");
				sb.Append (member.Name);
				return sb.ToString ();
			case SymbolKind.Event:
				sb = new StringBuilder ();
				sb.Append ("E:");
				sb.Append (member.DeclaringTypeDefinition.ReflectionName);
				sb.Append (".");
				sb.Append (member.Name);
				return sb.ToString ();
			case SymbolKind.Operator: // todo
				return "todo";
			}
			return "unknown entity: " + member;
		}

		static string GetIdString (MethodDefinition methodDefinition)
		{
			var sb = new StringBuilder ();
			sb.Append ("M:");
			sb.Append (methodDefinition.FullName);
			if (methodDefinition.HasGenericParameters) {
				sb.Append ("`");
				sb.Append (methodDefinition.GenericParameters.Count);
			}
//			AppendHelpParameterList (sb, method.Parameters);
			return sb.ToString ();
		}

		static string GetIdString (TypeDefinition typeDefinition)
		{
			if (!typeDefinition.HasGenericParameters)
				return "T:" + typeDefinition.FullName;
			return "T:" + typeDefinition.FullName + "`" + typeDefinition.GenericParameters.Count;
		}		

		bool IsMatch (ITreeNavigator nav, string helpUrl, bool searchType)
		{
			var member = nav.DataItem as IUnresolvedEntity;
			if (member == null)
				return false;
			
			if (searchType) {
				if (member is IUnresolvedTypeDefinition)
					return GetIdString (member) == helpUrl;
			} else {
				if (member is IUnresolvedMember) {
					return GetIdString (member) == helpUrl;
				}
			}
			return false;
		}
			
		static bool SkipChildren (ITreeNavigator nav, string helpUrl, bool searchType)
		{
			if (nav.DataItem is IUnresolvedMember)
				return true;
			if (nav.DataItem is BaseTypeFolder)
				return true;
			if (nav.DataItem is Project)
				return true;
			if (nav.DataItem is AssemblyReferenceFolder)
				return true;
			if (nav.DataItem is AssemblyResourceFolder)
				return true;
			string strippedUrl = helpUrl;
			if (strippedUrl.Length > 2 && strippedUrl [1] == ':')
				strippedUrl = strippedUrl.Substring (2);
			int idx = strippedUrl.IndexOf ('~');
			if (idx > 0) 
				strippedUrl = strippedUrl.Substring (0, idx);
			
			var type = nav.DataItem as IUnresolvedTypeDefinition;
			if (type != null && !strippedUrl.StartsWith (type.FullName, StringComparison.Ordinal))
				return true;
			if (nav.DataItem is Namespace && !strippedUrl.StartsWith (((Namespace)nav.DataItem).Name, StringComparison.Ordinal))
				return true;
			return false;
		}
		
		ITreeNavigator SearchMember (ITreeNavigator nav, string helpUrl, bool expandNode = true)
		{
			if (nav == null)
				return null;
			bool searchType = helpUrl.StartsWith ("T:", StringComparison.Ordinal);
			do {
				if (IsMatch (nav, helpUrl, searchType)) {
					inspectEditor.ClearSelection ();
					nav.ExpandToNode ();
					if (expandNode) {
						nav.Selected = nav.Expanded = true;
						nav.ScrollToNode ();
					} else {
						nav.Selected = true;
						nav.ScrollToNode ();
					}
					return nav;
				}
				if (!SkipChildren (nav, helpUrl, searchType) && nav.HasChildren ()) {
					nav.MoveToFirstChild ();
					ITreeNavigator result = SearchMember (nav, helpUrl, expandNode);
					if (result != null)
						return result;
					
					if (!nav.MoveToParent ()) {
						return null;
					}
					try {
						if (nav.DataItem is TypeDefinition && PublicApiOnly) {
							nav.MoveToFirstChild ();
							result = SearchMember (nav, helpUrl, expandNode);
							if (result != null)
								return result;
							nav.MoveToParent ();
						}
					} catch (Exception) {
					}
				}
			} while (nav.MoveNext());
			return null;
		}
		
		enum SearchMode 
		{
			Type   = 0,
			Member = 1,
			Disassembler = 2,
			Decompiler = 3,
			TypeAndMembers = 4
		}
		
		SearchMode searchMode = SearchMode.Type;
		Gtk.ListStore memberListStore;
		Gtk.ListStore typeListStore;
		
		void CreateColumns ()
		{
			foreach (TreeViewColumn column in searchTreeview.Columns) {
				searchTreeview.RemoveColumn (column);
			}
			TreeViewColumn col;
			Gtk.CellRenderer crp, crt;
			switch (searchMode) {
			case SearchMode.Member:
			case SearchMode.Disassembler:
			case SearchMode.Decompiler:
				col = new TreeViewColumn ();
				col.Title = GettextCatalog.GetString ("Member");
				crp = new CellRendererImage ();
				crt = new Gtk.CellRendererText ();
				col.PackStart (crp, false);
				col.PackStart (crt, true);
				col.AddAttribute (crp, "image", 0);
				col.AddAttribute (crt, "text", 1);
				col.SortColumnId = 1;
				searchTreeview.AppendColumn (col);
				col.Resizable = true;
				col = searchTreeview.AppendColumn (GettextCatalog.GetString ("Declaring Type"), new Gtk.CellRendererText (), "text", 2);
				col.SortColumnId = 2;
				col.Resizable = true;
				col = searchTreeview.AppendColumn (GettextCatalog.GetString ("Assembly"), new Gtk.CellRendererText (), "text", 3);
				col.SortColumnId = 3;
				col.Resizable = true;
				searchTreeview.Model = memberListStore;
				break;
			case SearchMode.TypeAndMembers:
				col = new TreeViewColumn ();
				col.Title = GettextCatalog.GetString ("Results");
				crp = new CellRendererImage ();
				crt = new Gtk.CellRendererText ();
				col.PackStart (crp, false);
				col.PackStart (crt, true);
				col.AddAttribute (crp, "image", 0);
				col.AddAttribute (crt, "text", 1);
				col.SortColumnId = 1;

				searchTreeview.AppendColumn (col);
				col.Resizable = true;
				col = searchTreeview.AppendColumn (GettextCatalog.GetString ("Parent"), new Gtk.CellRendererText (), "text", 2);
				col.SortColumnId = 2;

				col.Resizable = true;
				col = searchTreeview.AppendColumn (GettextCatalog.GetString ("Assembly"), new Gtk.CellRendererText (), "text", 3);
				col.SortColumnId = 3;

				col.Resizable = true;
				searchTreeview.Model = typeListStore;
				break;
			case SearchMode.Type:
				col = new TreeViewColumn ();
				col.Title = GettextCatalog.GetString ("Type");
				crp = new CellRendererImage ();
				crt = new Gtk.CellRendererText ();
				col.PackStart (crp, false);
				col.PackStart (crt, true);
				col.AddAttribute (crp, "image", 0);
				col.AddAttribute (crt, "text", 1);
				col.SortColumnId = 1;
				searchTreeview.AppendColumn (col);
				col.Resizable = true;

				col = searchTreeview.AppendColumn (GettextCatalog.GetString ("Namespace"), new Gtk.CellRendererText (), "text", 2);
				col.SortColumnId = 2;
				col.Resizable = true;

				col = searchTreeview.AppendColumn (GettextCatalog.GetString ("Assembly"), new Gtk.CellRendererText (), "text", 3);
				col.SortColumnId = 3;
				col.Resizable = true;
				searchTreeview.Model = typeListStore;
				break;
			}
		}
		System.ComponentModel.BackgroundWorker searchBackgoundWorker = null;

		public void StartSearch ()
		{
			string query = searchentry1.Query;
			if (searchBackgoundWorker != null && searchBackgoundWorker.IsBusy)
				searchBackgoundWorker.CancelAsync ();
			
			if (string.IsNullOrEmpty (query)) {
				notebook1.Page = 0;
				return;
			}
			
			this.notebook1.Page = 1;
			
			switch (searchMode) {
			case SearchMode.Member:
				IdeApp.Workbench.StatusBar.BeginProgress (GettextCatalog.GetString ("Searching member..."));
				break;
			case SearchMode.Disassembler:
				IdeApp.Workbench.StatusBar.BeginProgress (GettextCatalog.GetString ("Searching string in disassembled code..."));
				break;
			case SearchMode.Decompiler:
				IdeApp.Workbench.StatusBar.BeginProgress (GettextCatalog.GetString ("Searching string in decompiled code..."));
				break;
			case SearchMode.Type:
				IdeApp.Workbench.StatusBar.BeginProgress (GettextCatalog.GetString ("Searching type..."));
				break;
				case SearchMode.TypeAndMembers:
		       	IdeApp.Workbench.StatusBar.BeginProgress (GettextCatalog.GetString ("Searching types and members..."));
				break;
			}
			memberListStore.Clear ();
			typeListStore.Clear ();
			
			searchBackgoundWorker = new BackgroundWorker ();
			searchBackgoundWorker.WorkerSupportsCancellation = true;
			searchBackgoundWorker.WorkerReportsProgress = false;
			searchBackgoundWorker.DoWork += SearchDoWork;
			searchBackgoundWorker.RunWorkerCompleted += delegate {
				searchBackgoundWorker = null;
			};
			
			searchBackgoundWorker.RunWorkerAsync (query);
		}
	
		void SearchDoWork (object sender, DoWorkEventArgs e)
		{
			var publicOnly = PublicApiOnly;
			BackgroundWorker worker = sender as BackgroundWorker;
			try {
				string pattern = e.Argument.ToString ().ToUpper ();
				int types = 0, curType = 0;
				foreach (var unit in this.definitions) {
					types += unit.UnresolvedAssembly.TopLevelTypeDefinitions.Count ();
				}
				var memberDict = new Dictionary<AssemblyLoader, List<IUnresolvedMember>> ();
				switch (searchMode) {
				case SearchMode.Member:
					foreach (var unit in this.definitions) {
						var members = new List<IUnresolvedMember> ();
						foreach (var type in unit.UnresolvedAssembly.TopLevelTypeDefinitions) {
							if (worker.CancellationPending)
								return;
							if (!type.IsPublic && publicOnly)
								continue;
							curType++;
							foreach (var member in type.Members) {
								if (worker.CancellationPending)
									return;
								if (!member.IsPublic && publicOnly)
									continue;
								if (member.Name.ToUpper ().Contains (pattern)) {
									members.Add (member);
								}
							}
						}
						memberDict [unit] = members;
					}
					Gtk.Application.Invoke (delegate {
						IdeApp.Workbench.StatusBar.SetProgressFraction ((double)curType / types);
						foreach (var kv in memberDict) {
							foreach (var member in kv.Value) {
								if (worker.CancellationPending)
									return;
								memberListStore.AppendValues (ImageService.GetIcon (member.GetStockIcon (), Gtk.IconSize.Menu),
								                              member.Name,
								                              member.DeclaringTypeDefinition.FullName,
								                              kv.Key.Assembly.FullName,
								                              member);
							}
						}
					}
					);
					break;
				case SearchMode.Disassembler:
					Gtk.Application.Invoke (delegate {
						IdeApp.Workbench.StatusBar.BeginProgress (GettextCatalog.GetString ("Searching string in disassembled code..."));
					}
					);
					foreach (var unit in this.definitions) {
						foreach (var type in unit.UnresolvedAssembly.TopLevelTypeDefinitions) {
							if (worker.CancellationPending)
								return;
							curType++;
							foreach (var method in type.Methods) {
								if (worker.CancellationPending)
									return;
//								if (DomMethodNodeBuilder.Disassemble (rd => rd.DisassembleMethod (method)).ToUpper ().Contains (pattern)) {
//									members.Add (method);
//								}
							}
						}
					}
					Gtk.Application.Invoke (delegate {
						IdeApp.Workbench.StatusBar.SetProgressFraction ((double)curType / types);
						foreach (var kv in memberDict) {
							foreach (var member in kv.Value) {
								if (worker.CancellationPending)
									return;
								memberListStore.AppendValues ("", //iImageService.GetIcon (member.StockIcon, Gtk.IconSize.Menu),
								                              member.Name,
								                              member.DeclaringTypeDefinition.FullName,
								                              kv.Key.Assembly.FullName,
								                              member);
							}
						}
					}
					);
					break;
				case SearchMode.Decompiler:
					foreach (var unit in this.definitions) {
						foreach (var type in unit.UnresolvedAssembly.TopLevelTypeDefinitions) {
							if (worker.CancellationPending)
								return;
							curType++;
							foreach (var method in type.Methods) {
								if (worker.CancellationPending)
									return;
/*								if (DomMethodNodeBuilder.Decompile (domMethod, false).ToUpper ().Contains (pattern)) {
									members.Add (method);*/
							}
						}
					}
					Gtk.Application.Invoke (delegate {
						IdeApp.Workbench.StatusBar.SetProgressFraction ((double)curType / types);
						foreach (var kv in memberDict) {
							foreach (var member in kv.Value) {
								if (worker.CancellationPending)
									return;
								memberListStore.AppendValues ("", //ImageService.GetIcon (member.StockIcon, Gtk.IconSize.Menu),
								                              member.Name,
								                              member.DeclaringTypeDefinition.FullName,
								                              kv.Key.Assembly.FullName,
								                              member);
							}
						}
					}
					);
					break;
				case SearchMode.Type:
					var typeDict = new Dictionary<AssemblyLoader, List<IUnresolvedTypeDefinition>> ();
					foreach (var unit in this.definitions) {
						var typeList = new List<IUnresolvedTypeDefinition> ();
						foreach (var type in unit.UnresolvedAssembly.TopLevelTypeDefinitions) {
							if (worker.CancellationPending)
								return;
							if (!type.IsPublic && publicOnly)
								continue;
							if (type.FullName.ToUpper ().IndexOf (pattern, StringComparison.Ordinal) >= 0)
								typeList.Add (type);
						}
						typeDict [unit] = typeList;
					}
					Gtk.Application.Invoke (delegate {
						foreach (var kv in typeDict) {
							foreach (var type in kv.Value) {
								if (worker.CancellationPending)
									return;
								typeListStore.AppendValues (ImageService.GetIcon (type.GetStockIcon (), Gtk.IconSize.Menu),
								                            type.Name,
								                            type.Namespace,
								                            kv.Key.Assembly.FullName,
								                            type);
							}
						}
					});
					
					break;
				case SearchMode.TypeAndMembers:
					var typeDict2 = new Dictionary<AssemblyLoader, List<Tuple<IUnresolvedEntity, string>>> ();
					foreach (var unit in this.definitions) {
						var typeList = new List<Tuple<IUnresolvedEntity, string>> ();
						foreach (var type in unit.UnresolvedAssembly.TopLevelTypeDefinitions) {
							if (worker.CancellationPending)
								return;
							if (!type.IsPublic && publicOnly)
								continue;
							var parent = type.FullName;
							if (parent.ToUpper ().IndexOf (pattern, StringComparison.Ordinal) >= 0)
								typeList.Add (Tuple.Create ((IUnresolvedEntity)type, type.Namespace));
							
							foreach (var member in type.Members) {
								if (worker.CancellationPending)
									return;
								if (!member.IsPublic && publicOnly)
									continue;
								if (member.Name.ToUpper ().Contains (pattern)) {
									typeList.Add (Tuple.Create ((IUnresolvedEntity)member, parent));
								}
							}

						}
						typeDict2 [unit] = typeList;
					}

					Gtk.Application.Invoke (delegate {
						foreach (var kv in typeDict2) {
							foreach (var tuple in kv.Value) {
								if (worker.CancellationPending)
									return;
								var type = tuple.Item1;
								typeListStore.AppendValues (ImageService.GetIcon (type.GetStockIcon (), Gtk.IconSize.Menu),
								                        type.Name,
								                        tuple.Item2,
														kv.Key.Assembly.FullName,
								                        type);
							}
						}
					});

					break;
				}
			} finally {
				Gtk.Application.Invoke (delegate {
					IdeApp.Workbench.StatusBar.EndProgress ();
					IdeApp.Workbench.StatusBar.ShowReady ();
				});
			}
		}
		
		static bool preformat = false;
		internal static string FormatText (string text)
		{
			if (preformat)
				return text;
			StringBuilder result = new StringBuilder ();
			bool wasWhitespace = false;
			foreach (char ch in text) {
				switch (ch) {
					case '\n':
					case '\r':
						break;
					case '<':
						result.Append ("&lt;");
						break;
					case '>':
						result.Append ("&gt;");
						break;
					case '&':
						result.Append ("&amp;");
						break;
					default:
						if (wasWhitespace && Char.IsWhiteSpace (ch))
							break;
						wasWhitespace = Char.IsWhiteSpace (ch);
						result.Append (ch);
						break;
				}
			}
			return result.ToString ();
		}
		
		static void OutputChilds (StringBuilder sb, XmlNode node)
		{
			foreach (XmlNode child in node.ChildNodes) {
				OutputNode (sb, child);
			}
		}
		static void OutputNode (StringBuilder sb, XmlNode node)
		{
			if (node is XmlText) {
				sb.Append (FormatText (node.InnerText));
			} else if (node is XmlElement) {
				XmlElement el = node as XmlElement;
				switch (el.Name) {
					case "block":
						switch (el.GetAttribute ("type")) {
						case "note":
							sb.AppendLine ("<i>Note:</i>");
							break;
						case "behaviors":
							sb.AppendLine ("<b>Operation</b>");
							break;
						case "overrides":
							sb.AppendLine ("<b>Note to Inheritors</b>");
							break;
						case "usage":
							sb.AppendLine ("<b>Usage</b>");
							break;
						case "default":
							sb.AppendLine ();
							break;
						default:
							sb.Append ("<b>");
							sb.Append (el.GetAttribute ("type"));
							sb.AppendLine ("</b>");
							break;
						}
						OutputChilds (sb, node);
						return;
					case "c":
						preformat = true;
						sb.Append ("<tt>");
						OutputChilds (sb, node);
						sb.Append ("</tt>");
						preformat = false;
						return;
					case "code":
						preformat = true;
						sb.Append ("<tt>");
						OutputChilds (sb, node);
						sb.Append ("</tt>");
						preformat = false;
						return;
					case "exception":
						OutputChilds (sb, node);
						return;
					case "list":
						switch (el.GetAttribute ("type")) {
						case "table": // todo: table.
						case "bullet":
							foreach (XmlNode child in node.ChildNodes) {
								sb.Append ("    <b>*</b> ");
								OutputNode (sb, child);
							}
							break;
						case "number":
							int i = 1;
							foreach (XmlNode child in node.ChildNodes) {
								sb.Append ("    <b>" + i++ +"</b> ");
								OutputNode (sb, child);
							}
							break;
						default:
							OutputChilds (sb, node);
							break;
						}
						return;
					case "para":
						OutputChilds (sb, node);
						sb.AppendLine ();
						return;
					case "paramref":
						sb.Append (el.GetAttribute ("name"));
						return;
					case "permission":
						sb.Append (el.GetAttribute ("cref"));
						return;
					case "see":
						sb.Append ("<u>");
						sb.Append (el.GetAttribute ("langword"));
						sb.Append (el.GetAttribute ("cref"));
						sb.Append (el.GetAttribute ("internal"));
						sb.Append (el.GetAttribute ("topic"));
						sb.Append ("</u>");
						return;
					case "seealso":
						sb.Append ("<u>");
						sb.Append (el.GetAttribute ("langword"));
						sb.Append (el.GetAttribute ("cref"));
						sb.Append (el.GetAttribute ("internal"));
						sb.Append (el.GetAttribute ("topic"));
						sb.Append ("</u>");
						return;
				}
			}
			
			OutputChilds (sb, node);
		}
		
		static string TransformDocumentation (XmlNode docNode)
		{ 
			// after 3 hours to try it with xsl-t I decided to do the transformation in code.
			if (docNode == null)
				return null;
			StringBuilder result = new StringBuilder ();
			XmlNode node = docNode.SelectSingleNode ("summary");
			if (node != null) {
				OutputChilds (result, node);
				result.AppendLine ();
			}
			
			XmlNodeList nodes = docNode.SelectNodes ("param");
			if (nodes != null && nodes.Count > 0) {
				result.Append ("<big><b>Parameters</b></big>");
				foreach (XmlNode paraNode in nodes) {
					result.AppendLine ();
					result.AppendLine ("  <i>" + paraNode.Attributes["name"].InnerText +  "</i>");
					result.Append ("    ");
					OutputChilds (result, paraNode);
				}
				result.AppendLine ();
			}
			
			node = docNode.SelectSingleNode ("value");
			if (node != null) {
				result.AppendLine ("<big><b>Value</b></big>");
				OutputChilds (result, node);
				result.AppendLine ();
			}
			
			node = docNode.SelectSingleNode ("returns");
			if (node != null) {
				result.AppendLine ("<big><b>Returns</b></big>");
				OutputChilds (result, node);
				result.AppendLine ();
			}
				
			node = docNode.SelectSingleNode ("remarks");
			if (node != null) {
				result.AppendLine ("<big><b>Remarks</b></big>");
				OutputChilds (result, node);
				result.AppendLine ();
			}
				
			node = docNode.SelectSingleNode ("example");
			if (node != null) {
				result.AppendLine ("<big><b>Example</b></big>");
				OutputChilds (result, node);
				result.AppendLine ();
			}
				
			node = docNode.SelectSingleNode ("seealso");
			if (node != null) {
				result.AppendLine ("<big><b>See also</b></big>");
				OutputChilds (result, node);
				result.AppendLine ();
			}
			
			return result.ToString ();
		}
	 
		List<ReferenceSegment> ReferencedSegments = new List<ReferenceSegment>();
		List<ITextSegmentMarker> underlineMarkers = new List<ITextSegmentMarker> ();
		
		public void ClearReferenceSegment ()
		{
			ReferencedSegments = null;
			underlineMarkers.ForEach (m => inspectEditor.RemoveMarker (m));
			underlineMarkers.Clear ();
		}
		
		internal void SetReferencedSegments (List<ReferenceSegment> refs)
		{
			ReferencedSegments = refs;
			if (ReferencedSegments == null)
				return;
			foreach (var _seg in refs) {
				var seg = _seg;
				var line = inspectEditor.GetLineByOffset (seg.Offset);
				if (line == null)
					continue;
				// FIXME: ILSpy sometimes gives reference segments for punctuation. See http://bugzilla.xamarin.com/show_bug.cgi?id=2918
				string text = inspectEditor.GetTextAt (seg.Offset, seg.Length);
				if (text != null && text.Length == 1 && !(char.IsLetter (text [0]) || text [0] == '…'))
					continue;
				var marker = TextMarkerFactory.CreateLinkMarker (inspectEditor, seg.Offset, seg.Length, delegate (LinkRequest request) {
					bool? isNotPublic;
					var link = GetLink (seg, out isNotPublic);
					if (link == null)
						return;
					if (isNotPublic.HasValue) {
						if (isNotPublic.Value) {
							PublicApiOnly = false;
						}
					} else {
						// unable to determine if the member is public or not (in case of member references) -> try to search
						var nav = SearchMember (link, false);
						if (nav == null)
							PublicApiOnly = false;
					}
					var loader = (AssemblyLoader)this.TreeView.GetSelectedNode ().GetParentDataItem (typeof(AssemblyLoader), true);
					// args.Button == 2 || (args.Button == 1 && (args.ModifierState & Gdk.ModifierType.ShiftMask) == Gdk.ModifierType.ShiftMask)
					if (request == LinkRequest.RequestNewView) {
						AssemblyBrowserViewContent assemblyBrowserView = new AssemblyBrowserViewContent ();
						foreach (var cu in definitions) {
							assemblyBrowserView.Load (cu.UnresolvedAssembly.AssemblyName);
						}
						IdeApp.Workbench.OpenDocument (assemblyBrowserView, true);
						((AssemblyBrowserWidget)assemblyBrowserView.Control).Open (link);
					} else {
						this.Open (link, loader);
					}
				});
				underlineMarkers.Add (marker);
				inspectEditor.AddMarker (marker);
			}
		}

		void FillInspectLabel ()
		{
			ITreeNavigator nav = TreeView.GetSelectedNode ();
			if (nav == null)
				return;
			IAssemblyBrowserNodeBuilder builder = nav.TypeNodeBuilder as IAssemblyBrowserNodeBuilder;
			if (builder == null) {
				this.inspectEditor.Text = "";
				return;
			}
			
			ClearReferenceSegment ();
			inspectEditor.SetFoldings (Enumerable.Empty<IFoldSegment> ());
			switch (this.languageCombobox.Active) {
			case 0:
				inspectEditor.Options = DefaultSourceEditorOptions.PlainEditor;
				this.inspectEditor.MimeType = "text/x-csharp";
				SetReferencedSegments (builder.GetSummary (inspectEditor, nav, PublicApiOnly));
				break;
			case 1:
				inspectEditor.Options = DefaultSourceEditorOptions.PlainEditor;
				this.inspectEditor.MimeType = "text/x-ilasm";
				SetReferencedSegments (builder.Disassemble (inspectEditor, nav));
				break;
			case 2:
				inspectEditor.Options = DefaultSourceEditorOptions.PlainEditor;
				this.inspectEditor.MimeType = "text/x-csharp";
				SetReferencedSegments (builder.Decompile (inspectEditor, nav, PublicApiOnly));
				break;
			default:
				inspectEditor.Options = DefaultSourceEditorOptions.PlainEditor;
				this.inspectEditor.Text = "Invalid combobox value: " + this.languageCombobox.Active;
				break;
			}
		}
			
		void CreateOutput ()
		{
			ITreeNavigator nav = TreeView.GetSelectedNode ();
			
			if (nav != null) {
				IMember member = nav.DataItem as IMember;
				string documentation = GettextCatalog.GetString ("No documentation available.");
				if (member != null) {
					try {
						XmlNode node = member.GetMonodocDocumentation ();
						if (node != null) {
							documentation = TransformDocumentation (node) ?? documentation;
							/*
							StringWriter writer = new StringWriter ();
							XmlTextWriter w = new XmlTextWriter (writer);
							node.WriteTo (w);
							System.Console.WriteLine ("---------------------------");
							System.Console.WriteLine (writer);*/
							
						}
					} catch (Exception) {
					}
				}
//				this.documentationLabel.Markup = documentation;
/*				IAssemblyBrowserNodeBuilder builder = nav.TypeNodeBuilder as IAssemblyBrowserNodeBuilder;
				if (builder != null) {
					this.descriptionLabel.Markup  = builder.GetDescription (nav);
				} else {
					this.descriptionLabel.Markup = "";
				}*/
				
			}
			FillInspectLabel ();
		}
			
		/*int oldSize = -1;
		void VPaneExpose (object sender, Gtk.ExposeEventArgs args)
		{
			int size = this.vpaned1.Allocation.Height - 96;
			if (size == oldSize)
				return;
			this.vpaned1.Position = oldSize = size;
		}*/
		int oldSize2 = -1;
		void HPaneExpose (object sender, Gtk.ExposeEventArgs args)
		{
			int size = this.Allocation.Width;
			if (size == oldSize2)
				return;
			oldSize2 = size;
			this.hpaned1.Position = Math.Min (350, this.Allocation.Width * 2 / 3);
		}
		
		internal void Open (string url, AssemblyLoader currentAssembly = null, bool expandNode = true)
		{
			Task.WhenAll (this.definitions.Select (d => d.LoadingTask).ToArray ()).ContinueWith (d => {
				Application.Invoke (delegate {
					suspendNavigation = false;
					ITreeNavigator nav = SearchMember (url, expandNode);
					if (definitions == null) // we've been disposed
						return;
					if (nav != null)
						return;
					try {
						if (currentAssembly != null) {
							OpenFromAssembly (url, currentAssembly);
						} else {
							OpenFromAssemblyNames (url);
						}
					} catch (Exception e) {
						LoggingService.LogError ("Error while opening the assembly browser with id:" + url, e);
					}
				});
			});
		}

		void OpenFromAssembly (string url, AssemblyLoader currentAssembly, bool expandNode = true)
		{
			var cecilObject = loader.GetCecilObject (currentAssembly.UnresolvedAssembly);
			if (cecilObject == null)
				return;

			int i = 0;
			System.Action loadNext = null;
			var references = cecilObject.MainModule.AssemblyReferences;
			loadNext = () => {
				var reference = references [i];
				string fileName = currentAssembly.LookupAssembly (reference.FullName);
				if (string.IsNullOrEmpty (fileName)) {
					LoggingService.LogWarning ("Assembly browser: Can't find assembly: " + reference.FullName + ".");
					if (++i == references.Count)
						LoggingService.LogError ("Assembly browser: Can't find: " + url + ".");
					else
						loadNext ();
					return;
				}
				var result = AddReferenceByFileName (fileName);
				result.LoadingTask.ContinueWith (t2 => {
					if (definitions == null) // disposed
						return;
					Application.Invoke (delegate {
						var nav = SearchMember (url, expandNode);
						if (nav == null) {
							if (++i == references.Count)
								LoggingService.LogError ("Assembly browser: Can't find: " + url + ".");
							else
								loadNext ();
						}
					});
				}, TaskScheduler.Current);
			};
		}

		void OpenFromAssemblyNames (string url)
		{
			var tasks = new List<Task> ();
			foreach (var definition in definitions.ToArray ()) {
				var cecilObject = loader.GetCecilObject (definition.UnresolvedAssembly);
				if (cecilObject == null) {
					LoggingService.LogWarning ("Assembly browser: Can't find assembly: " + definition.UnresolvedAssembly.FullAssemblyName + ".");
					continue;
				}
				foreach (var assemblyNameReference in cecilObject.MainModule.AssemblyReferences) {
					var result = AddReferenceByAssemblyName (assemblyNameReference);
					if (result == null) {
						LoggingService.LogWarning ("Assembly browser: Can't find assembly: " + assemblyNameReference.FullName + ".");
					} else {
						tasks.Add (result.LoadingTask);
					}
				}
			}
			if (tasks.Count == 0) {
				var nav = SearchMember (url);
				if (nav == null) {
					LoggingService.LogError ("Assembly browser: Can't find: " + url + ".");
				}
				return;
			};
			Task.Factory.ContinueWhenAll (tasks.ToArray (), tarr => {
				var exceptions = tarr.Where (t => t.IsFaulted).Select (t => t.Exception).ToArray ();
				if (exceptions != null) {
					var ex = new AggregateException (exceptions).Flatten ();
					if (ex.InnerExceptions.Count > 0) {
						foreach (var inner in ex.InnerExceptions) {
							LoggingService.LogError ("Error while loading assembly in the browser.", inner);
						}
						throw ex;
					}
				}
				if (definitions == null) // disposed
					return;
				Application.Invoke (delegate {
					var nav = SearchMember (url);
					if (nav == null) {
						LoggingService.LogError ("Assembly browser: Can't find: " + url + ".");
					}
				});
			}, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Current);
		}
		
		public void SelectAssembly (string fileName)
		{
			AssemblyDefinition cu = null;
			foreach (var unit in definitions) {
				if (unit.UnresolvedAssembly.AssemblyName == fileName)
					cu = loader.GetCecilObject (unit.UnresolvedAssembly);
			}
			if (cu == null)
				return;
			
			ITreeNavigator nav = TreeView.GetRootNode ();
			if (nav == null)
				return;
			
			do {
				if (nav.DataItem == cu) {
					nav.ExpandToNode ();
					nav.Selected = true;
					nav.ScrollToNode ();
					return;
				}
			} while (nav.MoveNext());
		}
		
		void Dispose (ITreeNavigator nav)
		{
			if (nav == null)
				return;
			IDisposable d = nav.DataItem as IDisposable;
			if (d != null) 
				d.Dispose ();
			if (nav.HasChildren ()) {
				nav.MoveToFirstChild ();
				do {
					Dispose (nav);
				} while (nav.MoveNext ());
				nav.MoveToParent ();
			}
		}
		
		protected override void OnDestroyed ()
		{
			ClearReferenceSegment ();
			if (searchBackgoundWorker != null && searchBackgoundWorker.IsBusy) {
				searchBackgoundWorker.CancelAsync ();
				searchBackgoundWorker.Dispose ();
				searchBackgoundWorker = null;
			}
			
			if (this.TreeView != null) {
				//	Dispose (TreeView.GetRootNode ());
				TreeView.SelectionChanged -= HandleCursorChanged;
				this.TreeView.Clear ();
				this.TreeView = null;
			}
			
			if (definitions != null) {
				foreach (var def in definitions)
					def.Dispose ();
				definitions.Clear ();
				definitions = null;
			}
			
			ActiveMember = null;
			if (memberListStore != null) {
				memberListStore.Dispose ();
				memberListStore = null;
			}
			
			if (typeListStore != null) {
				typeListStore.Dispose ();
				typeListStore = null;
			}
			
			if (documentationPanel != null) {
				documentationPanel.Destroy ();
				documentationPanel = null;
			}
//			if (inspectEditor != null) {
//				inspectEditor.TextViewMargin.GetLink = null;
//				inspectEditor.LinkRequest -= InspectEditorhandleLinkRequest;
//				inspectEditor.Destroy ();
//			}
			
			if (this.UIManager != null) {
				this.UIManager.Dispose ();
				this.UIManager = null;
			}

			this.loader = null;
			this.languageCombobox.Changed -= LanguageComboboxhandleChanged;
//			this.searchInCombobox.Changed -= SearchInComboboxhandleChanged;
//			this.searchEntry.Changed -= SearchEntryhandleChanged;
			this.searchTreeview.RowActivated -= SearchTreeviewhandleRowActivated;
			hpaned1.ExposeEvent -= HPaneExpose;
			base.OnDestroyed ();
		}
		
		static AssemblyDefinition ReadAssembly (string fileName)
		{
			ReaderParameters parameters = new ReaderParameters ();
//			parameters.AssemblyResolver = new SimpleAssemblyResolver (Path.GetDirectoryName (fileName));
			using (var stream = new System.IO.MemoryStream (System.IO.File.ReadAllBytes (fileName))) {
				return AssemblyDefinition.ReadAssembly (stream, parameters);
			}
		}
		
		CecilLoader loader;
		internal CecilLoader CecilLoader {
			get {
				return loader;
			}
		} 
		
		List<AssemblyLoader> definitions = new List<AssemblyLoader> ();
		List<Project> projects = new List<Project> ();
		
		internal AssemblyLoader AddReferenceByAssemblyName (AssemblyNameReference reference)
		{
			return AddReferenceByAssemblyName (reference.Name);
		}
		
		internal AssemblyLoader AddReferenceByAssemblyName (string assemblyFullName)
		{
			string assemblyFile = Runtime.SystemAssemblyService.DefaultAssemblyContext.GetAssemblyLocation (assemblyFullName, null);
			if (assemblyFile == null || !System.IO.File.Exists (assemblyFile)) {
				foreach (var wrapper in definitions) {
					assemblyFile = wrapper.LookupAssembly (assemblyFullName);
					if (assemblyFile != null && System.IO.File.Exists (assemblyFile))
						break;
				}
			}
			if (assemblyFile == null || !System.IO.File.Exists (assemblyFile))
				return null;
			
			return AddReferenceByFileName (assemblyFile);
		}
		
		internal AssemblyLoader AddReferenceByFileName (string fileName)
		{
			var result = definitions.FirstOrDefault (d => d.FileName == fileName);
			if (result != null) {
//				// Select the result.
//				if (selectReference) {
//					ITreeNavigator navigator = TreeView.GetNodeAtObject (result);
//					if (navigator != null) {
//						navigator.Selected = true;
//					} else {
//						LoggingService.LogWarning (result + " could not be found.");
//					}
//				}

				return result;
			}
			if (!File.Exists (fileName))
				return null;
			result = new AssemblyLoader (this, fileName);
			
			definitions.Add (result);
			result.LoadingTask = result.LoadingTask.ContinueWith (task => {
				Application.Invoke (delegate {
					if (definitions == null)
						return;
					try {
						ITreeBuilder builder;
						if (definitions.Count + projects.Count == 1) {
							builder = TreeView.LoadTree (result);
						} else {
							builder = TreeView.AddChild (result);
						}
						if (TreeView.GetSelectedNode () == null)
							builder.Selected = builder.Expanded = true;
					} catch (Exception e) {
						LoggingService.LogError ("Error while adding assembly to the assembly list", e);
					}
				});
				return task.Result;
			}
			                                                     );
			return result;
		}
		
		public void AddProject (Project project, bool selectReference = true)
		{
			if (project == null)
				throw new ArgumentNullException ("project");

			if (projects.Contains (project)) {
				// Select the project.
				if (selectReference) {
					ITreeNavigator navigator = TreeView.GetNodeAtObject (project);

					if (navigator != null)
						navigator.Selected = true;
				}

				return;
			}
			projects.Add (project);
			ITreeBuilder builder;
			if (definitions.Count + projects.Count == 1) {
				builder = TreeView.LoadTree (project);
			} else {
				builder = TreeView.AddChild (project);
			}
			builder.Selected = builder.Expanded = selectReference;
		}

		//MonoDevelop.Components.RoundedFrame popupWidgetFrame;

		#region NavigationHistory
		internal bool suspendNavigation;
		void HandleCursorChanged (object sender, EventArgs e)
		{
			if (!suspendNavigation) {
				var selectedEntity = TreeView.GetSelectedNode ()?.DataItem as IUnresolvedEntity;
				if (selectedEntity != null)
					NavigationHistoryService.LogActiveDocument ();
			}
			notebook1.Page = 0;
			CreateOutput ();
		}

		public NavigationPoint BuildNavigationPoint ()
		{
			var selectedEntity = TreeView.GetSelectedNode ()?.DataItem as IUnresolvedEntity;
			if (selectedEntity == null)
				return null;
			return new AssemblyBrowserNavigationPoint (definitions, GetIdString (selectedEntity));
		}
		#endregion

		internal void EnsureDefinitionsLoaded (List<AssemblyLoader> definitions)
		{
			if (definitions == null)
				throw new ArgumentNullException (nameof (definitions));
			foreach (var def in definitions) {
				if (!this.definitions.Contains (def)) {
					this.definitions.Add (def);
					Application.Invoke (delegate {
						if (definitions.Count + projects.Count == 1) {
							TreeView.LoadTree (def.LoadingTask.Result);
						} else {
							TreeView.AddChild (def.LoadingTask.Result);
						}
					});
				}
			}
		}
	}
}

