﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GitCommands;
using GitUI.Properties;
using GitUI.UserControls;
using ResourceManager;

namespace GitUI
{
    using GitItemsWithParents = IDictionary<string, IList<GitItemStatus>>;

    public sealed partial class FileStatusList : GitModuleControl
    {
        private readonly TranslationString _UnsupportedMultiselectAction =
            new TranslationString("Operation not supported");
        private readonly TranslationString _DiffWithParent =
            new TranslationString("Diff with parent");
        public readonly TranslationString CombinedDiff =
            new TranslationString("Combined Diff");

        private IDisposable selectedIndexChangeSubscription;
        private static readonly TimeSpan SelectedIndexChangeThrottleDuration = TimeSpan.FromMilliseconds(50);

        private const int ImageSize = 16;

        private bool _filterVisible;

        public FileStatusList()
        {
            InitializeComponent(); Translate();
            FilterVisible = false;

            SelectFirstItemOnSetItems = true;
            _noDiffFilesChangesDefaultText = NoFiles.Text;
#if !__MonoCS__ // TODO Drag'n'Drop doesn't work on Mono/Linux
            FileStatusListView.MouseMove += FileStatusListView_MouseMove;
            FileStatusListView.MouseDown += FileStatusListView_MouseDown;
#endif
            if (_images == null)
            {
                _images = new ImageList();
                _images.Images.Add(Resources.Removed); // 0
                _images.Images.Add(Resources.Added); // 1
                _images.Images.Add(Resources.Modified); // 2
                _images.Images.Add(Resources.Renamed); // 3
                _images.Images.Add(Resources.Copied); // 4
                _images.Images.Add(Resources.IconSubmoduleDirty); // 5
                _images.Images.Add(Resources.IconSubmoduleRevisionUp); // 6
                _images.Images.Add(Resources.IconSubmoduleRevisionUpDirty); // 7
                _images.Images.Add(Resources.IconSubmoduleRevisionDown); // 8
                _images.Images.Add(Resources.IconSubmoduleRevisionDownDirty); // 9
                _images.Images.Add(Resources.IconSubmoduleRevisionSemiUp); // 10
                _images.Images.Add(Resources.IconSubmoduleRevisionSemiUpDirty); // 11
                _images.Images.Add(Resources.IconSubmoduleRevisionSemiDown); // 12
                _images.Images.Add(Resources.IconSubmoduleRevisionSemiDownDirty); // 13
                _images.Images.Add(Resources.IconFileStatusUnknown); // 14
            }
            FileStatusListView.SmallImageList = _images;
            FileStatusListView.LargeImageList = _images;

            HandleVisibility_NoFilesLabel_FilterComboBox(filesPresent: true);
            this.Controls.SetChildIndex(NoFiles, 0);
            NoFiles.Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Italic);

            _filter = new Regex(".*");
        }

        protected override void DisposeCustomResources()
        {
            if (selectedIndexChangeSubscription != null)
            {
                selectedIndexChangeSubscription.Dispose();
            }
        }

        private void EnsureSelectedIndexChangeSubscription()
        {
            if (selectedIndexChangeSubscription == null)
            {
                selectedIndexChangeSubscription = Observable.FromEventPattern(
                    h => FileStatusListView.SelectedIndexChanged += h,
                    h => FileStatusListView.SelectedIndexChanged -= h)
                    .Throttle(SelectedIndexChangeThrottleDuration)
                    .ObserveOn(SynchronizationContext.Current)
                    .Subscribe(_ => FileStatusListView_SelectedIndexChanged());
            }
        }

        private static ImageList _images;

        private readonly string _noDiffFilesChangesDefaultText;

        public void SetNoFilesText(string text)
        {
            NoFiles.Text = text;
        }

        public string GetNoFilesText()
        {
            return NoFiles.Text;
        }

        public bool FilterVisible
        {
            get
            {
                return _filterVisible;
            }

            set
            {
                _filterVisible = value;
                FilterComboBox.Visible = _filterVisible;
                FilterWatermarkLabel.Visible = _filterVisible;
            }
        }

        public override bool Focused
        {
            get
            {
                return FileStatusListView.Focused;
            }
        }

        public new void Focus()
        {
            if (FileStatusListView.Items.Count > 0)
            {
                if (SelectedItem == null)
                    SelectedIndex = 0;
                FileStatusListView.Focus();
            }
        }

        public void BeginUpdate()
        {
            FileStatusListView.BeginUpdate();
        }

        public void EndUpdate()
        {
            FileStatusListView.EndUpdate();
        }

        private string GetItemText(Graphics graphics, GitItemStatus gitItemStatus)
        {
            var pathFormatter = new PathFormatter(graphics, FileStatusListView.Font);

            return pathFormatter.FormatTextForDrawing(FileStatusListView.ClientSize.Width - ImageSize,
                                                      gitItemStatus.Name, gitItemStatus.OldName);
        }

        private void FileStatusListView_DrawItem(object sender, DrawListViewItemEventArgs e)
        {
            if (e.Bounds.Height <= 0 || e.Bounds.Width <= 0 || e.ItemIndex < 0)
                return;

            e.DrawBackground();
            Color color;
            if (e.Item.Selected)
            {
                e.Graphics.FillRectangle(SystemBrushes.Highlight, e.Bounds);
                color = SystemColors.HighlightText;
            }
            else
                color = SystemColors.WindowText;
            e.DrawFocusRectangle();

            e.Graphics.FillRectangle(Brushes.White, e.Bounds.Left, e.Bounds.Top, ImageSize, e.Bounds.Height);

            int centeredImageTop = e.Bounds.Top;
            if ((e.Bounds.Height - ImageSize) > 1)
                centeredImageTop = e.Bounds.Top + ((e.Bounds.Height - ImageSize) / 2);

            var image = e.Item.ImageList.Images[e.Item.ImageIndex];

            if (image != null)
                e.Graphics.DrawImage(image, e.Bounds.Left, centeredImageTop, ImageSize, ImageSize);

            GitItemStatus gitItemStatus = (GitItemStatus)e.Item.Tag;

            string text = GetItemText(e.Graphics, gitItemStatus);

            if (gitItemStatus.IsSubmodule && gitItemStatus.SubmoduleStatus != null && gitItemStatus.SubmoduleStatus.IsCompleted)
                text += gitItemStatus.SubmoduleStatus.Result.AddedAndRemovedString();

            e.Graphics.DrawString(text, e.Item.ListView.Font,
                                  new SolidBrush(color), e.Bounds.Left + ImageSize, e.Bounds.Top);
        }

#if !__MonoCS__ // TODO Drag'n'Drop doesnt work on Mono/Linux
        void FileStatusListView_MouseDown(object sender, MouseEventArgs e)
        {
            //SELECT
            if (e.Button == MouseButtons.Right)
            {
                var hover = FileStatusListView.HitTest(e.Location);

                if (hover.Item != null && !hover.Item.Selected)
                {
                    ClearSelected();

                    hover.Item.Selected = true;
                }
            }

            //DRAG
            if (e.Button == MouseButtons.Left)
            {
                if (SelectedItems.Any())
                {
                    // Remember the point where the mouse down occurred.
                    // The DragSize indicates the size that the mouse can move
                    // before a drag event should be started.
                    Size dragSize = SystemInformation.DragSize;

                    // Create a rectangle using the DragSize, with the mouse position being
                    // at the center of the rectangle.
                    dragBoxFromMouseDown = new Rectangle(new Point(e.X - (dragSize.Width / 2),
                                                                   e.Y - (dragSize.Height / 2)),
                                                            dragSize);
                }
                else
                    // Reset the rectangle if the mouse is not over an item in the ListView.
                    dragBoxFromMouseDown = Rectangle.Empty;
            }
        }
#endif

        public override ContextMenuStrip ContextMenuStrip
        {
            get
            {
                return FileStatusListView.ContextMenuStrip;
            }
            set
            {
                FileStatusListView.ContextMenuStrip = value;
            }
        }

        public override ContextMenu ContextMenu
        {
            get
            {
                return FileStatusListView.ContextMenu;
            }
            set
            {
                FileStatusListView.ContextMenu = value;
            }
        }

#if !__MonoCS__ // TODO Drag'n'Drop doesnt work on Mono/Linux
        private Rectangle dragBoxFromMouseDown;

        void FileStatusListView_MouseMove(object sender, MouseEventArgs e)
        {
            ListView listView = sender as ListView;

            //DRAG
            // If the mouse moves outside the rectangle, start the drag.
            if (dragBoxFromMouseDown != Rectangle.Empty &&
                !dragBoxFromMouseDown.Contains(e.X, e.Y))
            {
                if (SelectedItems.Any())
                {
                    StringCollection fileList = new StringCollection();

                    foreach (GitItemStatus item in SelectedItems)
                    {
                        string fileName = Path.Combine(Module.WorkingDir, item.Name);

                        fileList.Add(fileName.ToNativePath());
                    }

                    DataObject obj = new DataObject();
                    obj.SetFileDropList(fileList);

                    // Proceed with the drag and drop, passing in the list item.
                    DoDragDrop(obj, DragDropEffects.Copy);
                    dragBoxFromMouseDown = Rectangle.Empty;
                }
            }

            //TOOLTIP
            if (listView != null)
            {
                var point = new Point(e.X, e.Y);
                var hover = listView.HitTest(point);
                if (hover.Item != null)
                {
                    var gitItemStatus = (GitItemStatus)hover.Item.Tag;

                    string text;
                    if (gitItemStatus.IsRenamed || gitItemStatus.IsCopied)
                        text = string.Concat(gitItemStatus.Name, " (", gitItemStatus.OldName, ")");
                    else
                        text = gitItemStatus.Name;

                    float fTextWidth = listView.CreateGraphics().MeasureString(text, listView.Font).Width + 17;

                    //Use width-itemheight because the icon drawn in front of the text is the itemheight
                    if (fTextWidth > (FileStatusListView.Width - FileStatusListView.GetItemRect(hover.Item.Index).Height))
                    {
                        if (!hover.Item.ToolTipText.Equals(gitItemStatus.ToString()))
                            hover.Item.ToolTipText = gitItemStatus.ToString();
                    }
                    else
                        hover.Item.ToolTipText = "";
                }
            }
        }
#endif

        [Browsable(false)]
        public IEnumerable<GitItemStatus> AllItems
        {
            get
            {
                return (FileStatusListView.Items.Cast<ListViewItem>().
                    Select(selectedItem => (GitItemStatus)selectedItem.Tag));
            }
        }

        [Browsable(false)]
        public IEnumerable<GitItemStatus> SelectedItems
        {
            get
            {
                return FileStatusListView.SelectedItems.Cast<ListViewItem>().
                    Select(i => (GitItemStatus)i.Tag);
            }
            set
            {
                ClearSelected();
                if (value == null)
                    return;

                foreach (var item in FileStatusListView.Items.Cast<ListViewItem>()
                    .Where(i => value.Contains((GitItemStatus)i.Tag)))
                {
                    item.Selected = true;
                }
                var first = FileStatusListView.SelectedItems.Cast<ListViewItem>().FirstOrDefault(x => x.Selected);
                if (first != null)
                    first.EnsureVisible();
                StoreNextIndexToSelect();
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public GitItemStatus SelectedItem
        {
            get
            {
                if (FileStatusListView.SelectedItems.Count > 0)
                {
                    ListViewItem item = FileStatusListView.SelectedItems[0];
                    return (GitItemStatus)item.Tag;
                }
                return null;
            }
            set
            {
                ClearSelected();
                if (value == null)
                    return;
                ListViewItem newSelected = null;
                foreach (ListViewItem item in FileStatusListView.Items)
                {
                    if (value.CompareTo((GitItemStatus)item.Tag) == 0)
                    {
                        if (newSelected == null)
                        {
                            newSelected = item;
                        }
                        else if (item.Tag == value)
                        {
                            newSelected = item;
                            break;
                        }

                    }
                }
                if (newSelected != null)
                {
                    newSelected.Selected = true;
                    newSelected.EnsureVisible();
                }
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public string SelectedItemParent
        {
            get
            {
                foreach (ListViewItem item in FileStatusListView.SelectedItems)
                    return item.Group != null ? (string)item.Group.Tag : null;
                return null;
            }
        }

        public void ClearSelected()
        {
            foreach (ListViewItem item in FileStatusListView.SelectedItems)
                item.Selected = false;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public int SelectedIndex
        {
            get
            {
                foreach (int i in FileStatusListView.SelectedIndices)
                    return i;
                return -1;
            }
            set
            {
                ClearSelected();
                if (value >= 0)
                {
                    FileStatusListView.Items[value].Selected = true;
                    FileStatusListView.Items[value].Focused = true;
                    FileStatusListView.Items[value].EnsureVisible();
                }
            }
        }

        private int _nextIndexToSelect = -1;

        public void StoreNextIndexToSelect()
        {
            _nextIndexToSelect = -1;
            foreach (int idx in FileStatusListView.SelectedIndices)
                if (idx > _nextIndexToSelect)
                    _nextIndexToSelect = idx;
            _nextIndexToSelect = _nextIndexToSelect - FileStatusListView.SelectedIndices.Count + 1;
        }

        public void SelectStoredNextIndex(int defaultIndex = -1)
        {
            _nextIndexToSelect = Math.Min(_nextIndexToSelect, FileStatusListView.Items.Count - 1);
            if (_nextIndexToSelect < 0 && defaultIndex > -1)
                _nextIndexToSelect = Math.Min(defaultIndex, FileStatusListView.Items.Count - 1);
            if (_nextIndexToSelect > -1)
                SelectedIndex = _nextIndexToSelect;
            _nextIndexToSelect = -1;
        }

        public event EventHandler SelectedIndexChanged;
        public event EventHandler DataSourceChanged;

        public new event EventHandler DoubleClick;
        public new event KeyEventHandler KeyDown;

        void FileStatusListView_DoubleClick(object sender, EventArgs e)
        {
            if (DoubleClick == null)
                UICommands.StartFileHistoryDialog(this, SelectedItem.Name, Revision);
            else
                DoubleClick(sender, e);
        }

        void FileStatusListView_SelectedIndexChanged()
        {
            if (SelectedIndexChanged != null)
                SelectedIndexChanged(this, EventArgs.Empty);
        }

        private static int GetItemImageIndex(GitItemStatus gitItemStatus)
        {
            if (gitItemStatus.IsDeleted)
                return 0;
            if (gitItemStatus.IsNew || !gitItemStatus.IsTracked)
                return 1;
            if (gitItemStatus.IsChanged || gitItemStatus.IsConflict)
            {
                if (!gitItemStatus.IsSubmodule || gitItemStatus.SubmoduleStatus == null ||
                    !gitItemStatus.SubmoduleStatus.IsCompleted)
                    return 2;

                var status = gitItemStatus.SubmoduleStatus.Result;
                if (status == null)
                    return 2;
                if (status.Status == SubmoduleStatus.FastForward)
                    return 6 + (status.IsDirty ? 1 : 0);
                if (status.Status == SubmoduleStatus.Rewind)
                    return 8 + (status.IsDirty ? 1 : 0);
                if (status.Status == SubmoduleStatus.NewerTime)
                    return 10 + (status.IsDirty ? 1 : 0);
                if (status.Status == SubmoduleStatus.OlderTime)
                    return 12 + (status.IsDirty ? 1 : 0);
                return !status.IsDirty ? 2 : 5;
            }
            if (gitItemStatus.IsRenamed)
                return 3;
            if (gitItemStatus.IsCopied)
                return 4;
            return 14;//icon unknown
        }

        [Browsable(false)]
        [DefaultValue(true)]
        public bool IsEmpty
        {
            get { return GitItemStatuses == null || !GitItemStatuses.Any(); }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public IList<GitItemStatus> GitItemStatuses
        {
            get
            {
                var result = new List<GitItemStatus>();
                var data = GitItemStatusesWithParents;
                if (data != null)
                    foreach (var plist in data.Values)
                        result.AddAll(plist);

                return result;
            }
            set
            {
                if (value == null)
                    GitItemStatusesWithParents = null;
                else
                    SetGitItemStatuses(null, value);
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public IList<GitItemStatus> GitItemFilteredStatuses
        {
            get
            {
                var result = new List<GitItemStatus>();
                foreach(ListViewItem listViewItem in FileStatusListView.Items)
                {
                    result.Add((GitItemStatus)listViewItem.Tag);
                }
                return result;
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public string GitFirstParent
        {
            get
            {
                var data = GitItemStatusesWithParents;
                if (data != null && data.Count > 0)
                    return data.ElementAt(0).Key;
                return null;
            }
        }

        public void SetGitItemStatuses(string parentRev, IList<GitItemStatus> items)
        {
            var dictionary = new Dictionary<string, IList<GitItemStatus>> { { parentRev ?? "", items } };
            GitItemStatusesWithParents = dictionary;
        }

        private GitItemsWithParents _itemsDictionary = new Dictionary<string, IList<GitItemStatus>>();
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public GitItemsWithParents GitItemStatusesWithParents
        {
            get
            {
                return _itemsDictionary;
            }
            set
            {
                _itemsDictionary = value;
                UpdateFileStatusListView();
            }
        }

        private void UpdateFileStatusListView(bool updateCausedByFilter = false)
        {
            if (_itemsDictionary == null || !_itemsDictionary.Any())
            {
                HandleVisibility_NoFilesLabel_FilterComboBox(filesPresent: false);
            }
            else
            {
                EnsureSelectedIndexChangeSubscription();
                HandleVisibility_NoFilesLabel_FilterComboBox(filesPresent: true);
            }

            var previouslySelectedItems = new List<GitItemStatus>();
            if (updateCausedByFilter)
            {
                foreach (ListViewItem Item in FileStatusListView.SelectedItems)
                {
                    previouslySelectedItems.Add((GitItemStatus)Item.Tag);
                }
            }

            FileStatusListView.Scrollable = false;

            FileStatusListView.BeginUpdate();
            FileStatusListView.ShowGroups = _itemsDictionary != null && _itemsDictionary.Count > 1;
            FileStatusListView.Groups.Clear();
            FileStatusListView.Items.Clear();
            if (_itemsDictionary != null)
            {
                var list = new List<ListViewItem>();
                foreach (var pair in _itemsDictionary)
                {
                    ListViewGroup group = null;
                    if (!String.IsNullOrEmpty(pair.Key))
                    {
                        var groupName = "";
                        if (pair.Key == CombinedDiff.Text)
                        {
                            groupName = CombinedDiff.Text;
                        }
                        else
                        {
                            string shortHash = pair.Key.Length > 8 ? pair.Key.Substring(0, 8) : pair.Key;
                            groupName = _DiffWithParent.Text + " " + shortHash;
                        }

                        group = new ListViewGroup(groupName);
                        group.Tag = pair.Key;
                        FileStatusListView.Groups.Add(group);
                    }
                    foreach (var item in pair.Value)
                    {
                        if (_filter.IsMatch(item.Name))
                        {
                            var listItem = new ListViewItem(item.Name, group);
                            listItem.ImageIndex = GetItemImageIndex(item);
                            if (item.SubmoduleStatus != null && !item.SubmoduleStatus.IsCompleted)
                            {
                                var capturedItem = item;
                                item.SubmoduleStatus.ContinueWith((task) => listItem.ImageIndex = GetItemImageIndex(capturedItem),
                                                                  CancellationToken.None,
                                                                  TaskContinuationOptions.OnlyOnRanToCompletion,
                                                                  TaskScheduler.FromCurrentSynchronizationContext());
                            }
                            if (previouslySelectedItems.Contains(item))
                            {
                                listItem.Selected = true;
                            }
                            listItem.Tag = item;
                            list.Add(listItem);
                        }
                    }
                }
                FileStatusListView.Items.AddRange(list.ToArray());
            }
            if (updateCausedByFilter == false)
            {
                FileStatusListView_SelectedIndexChanged();
                if (DataSourceChanged != null)
                    DataSourceChanged(this, new EventArgs());
                if (SelectFirstItemOnSetItems)
                    SelectFirstVisibleItem();
            }
            FileStatusListView_SizeChanged(null, null);
            FileStatusListView.SetGroupState(ListViewGroupState.Collapsible);
            FileStatusListView.EndUpdate();
            FileStatusListView.Scrollable = true;
        }

        [DefaultValue(true)]
        public bool SelectFirstItemOnSetItems { get; set; }

        public void SelectFirstVisibleItem()
        {
            if (FileStatusListView.Items.Count == 0)
                return;
            var group = FileStatusListView.Groups.Cast<ListViewGroup>().
                FirstOrDefault(gr => gr.Items.Count > 0);
            if (group != null)
            {
                ListViewItem sortedFirstGroupItem = FileStatusListView.Items.Cast<ListViewItem>().
                    FirstOrDefault(item => item.Group == group);
                if (sortedFirstGroupItem != null)
                    sortedFirstGroupItem.Selected = true;
            }
            else if (FileStatusListView.Items.Count > 0)
                FileStatusListView.Items[0].Selected = true;
        }

        /// <summary>
        /// Gets or sets the revision.
        /// </summary>
        /// <value>The revision.</value>
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        [Browsable(false)]
        public GitRevision Revision { get; set; }

        private void FileStatusListView_SizeChanged(object sender, EventArgs e)
        {
            NoFiles.Location = new Point(5, 5);
            NoFiles.Size = new Size(Size.Width - 10, Size.Height - 10);
            Refresh();
            FileStatusListView.BeginUpdate();

            FileStatusListView.AutoResizeColumn(0,
                ColumnHeaderAutoResizeStyle.HeaderSize);
            FileStatusListView.EndUpdate();
        }

        private void FileStatusListView_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.A:
                    {
                        if (!e.Control)
                            break;
                        FileStatusListView.BeginUpdate();
                        try
                        {
                            for (var i = 0; i < FileStatusListView.Items.Count; i++)
                                FileStatusListView.Items[i].Selected = true;
                            e.Handled = true;
                        }
                        finally
                        {
                            FileStatusListView.EndUpdate();
                        }
                        break;
                    }
                default:
                    if (KeyDown != null)
                        KeyDown(sender, e);
                    break;
            }
        }

        public int SetSelectionFilter(string selectionFilter)
        {
            return SelectFiles(RegexForSelecting(selectionFilter));
        }

        private static Regex RegexForSelecting(string value)
        {
            return string.IsNullOrEmpty(value)
                ? new Regex("^$", RegexOptions.Compiled)
                : new Regex(value, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        private int SelectFiles(Regex selctionFilter)
        {
            try
            {
                SuspendLayout();

                var items = AllItems;
                int i = 0;
                foreach (var item in items)
                {
                    FileStatusListView.Items[i].Selected = selctionFilter.IsMatch(item.Name);
                    i++;
                }

                return FileStatusListView.SelectedIndices.Count;
            }
            finally
            {
                ResumeLayout(true);
            }
        }

        public void SetDiffs(List<GitRevision> revisions)
        {
            HandleVisibility_NoFilesLabel_FilterComboBox(filesPresent: true);
            switch (revisions.Count)
            {
                case 0:
                    NoFiles.Text = _noDiffFilesChangesDefaultText;
                    GitItemStatuses = null;
                    break;

                case 1: // diff "parent" --> "selected revision"
                    SetDiff(revisions[0]);
                    break;

                case 2: // diff "first clicked revision" --> "second clicked revision"
                    NoFiles.Text = _noDiffFilesChangesDefaultText;
                    bool artificialRevSelected = revisions[0].IsArtificial() || revisions[1].IsArtificial();
                    if (artificialRevSelected)
                    {
                        NoFiles.Text = _UnsupportedMultiselectAction.Text;
                        GitItemStatuses = null;
                    }
                    else
                        SetGitItemStatuses(revisions[1].Guid, Module.GetDiffFilesWithSubmodulesStatus(revisions[0].Guid, revisions[1].Guid));
                    break;

                default: // more than 2 revisions selected => no diff
                    NoFiles.Text = _UnsupportedMultiselectAction.Text;
                    GitItemStatuses = null;
                    break;
            }
            UpdateNoFilesLabelVisibility();
        }

        private void UpdateNoFilesLabelVisibility()
        {
            if (GitItemStatusesWithParents == null && GitItemStatuses == null)
                HandleVisibility_NoFilesLabel_FilterComboBox(filesPresent: false);
            else if (GitItemStatusesWithParents != null)
            {
                List<string> keys = GitItemStatusesWithParents.Keys.ToList();
                if (keys.Count == 0)
                    HandleVisibility_NoFilesLabel_FilterComboBox(filesPresent: false);
                else if (keys.Count == 1 && (GitItemStatusesWithParents[keys[0]] == null || GitItemStatusesWithParents[keys[0]].Count == 0))
                    HandleVisibility_NoFilesLabel_FilterComboBox(filesPresent: false);
            }
            else if (GitItemStatuses != null)
            {
                if (GitItemStatuses.Count == 0)
                    HandleVisibility_NoFilesLabel_FilterComboBox(filesPresent: false);
            }
        }

        public void SetDiff(GitRevision revision)
        {
            NoFiles.Text = _noDiffFilesChangesDefaultText;

            Revision = revision;

            if (revision == null)
                GitItemStatuses = null;
            else if (revision.ParentGuids == null || revision.ParentGuids.Length == 0)
                GitItemStatuses = Module.GetTreeFiles(revision.TreeGuid, true);
            else
            {
                if (revision.Guid == GitRevision.UnstagedGuid) //working directory changes
                    GitItemStatuses = Module.GetUnstagedFilesWithSubmodulesStatus();
                else if (revision.Guid == GitRevision.IndexGuid) //index
                    GitItemStatuses = Module.GetStagedFilesWithSubmodulesStatus();
                else
                {
                    GitItemsWithParents dictionary = new Dictionary<string, IList<GitItemStatus>>();
                    foreach (var parentRev in revision.ParentGuids)
                    {
                        dictionary.Add(parentRev, Module.GetDiffFilesWithSubmodulesStatus(revision.Guid, parentRev));

                        //Only add the first parent to the dictionary if the setting to show diffs
                        //for app parents is disabled
                        if (!AppSettings.ShowDiffForAllParents)
                            break;
                    }
                    var isMergeCommit = revision.ParentGuids.Count() == 2;
                    if (isMergeCommit)
                    {
                        var conflicts = Module.GetCombinedDiffFileList(revision.Guid);
                        if (conflicts.Any())
                        {
                            dictionary.Add(CombinedDiff.Text, conflicts);
                        }
                    }
                    GitItemStatusesWithParents = dictionary;
                }
            }
        }

        private void HandleVisibility_NoFilesLabel_FilterComboBox(bool filesPresent)
        {
            NoFiles.Visible = !filesPresent;
            if (_filterVisible)
            {
                FilterComboBox.Visible = filesPresent;
            }
        }

        #region Filtering


        private long _lastUserInputTime;
        private string _ToolTipText = "";

        private static Regex RegexForFiltering(string value)
        {
            return string.IsNullOrEmpty(value)
                ? new Regex(".", RegexOptions.Compiled)
                : new Regex(value, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        }

        public void SetFilter(string value)
        {
            FilterComboBox.Text = value;
            FilterFiles(value);
        }

        private int FilterFiles(string value)
        {
            _filter = RegexForFiltering(value);
            UpdateFileStatusListView(true);
            return FileStatusListView.Items.Count;
        }

        private void FilterComboBox_TextUpdate(object sender, EventArgs e)
        {
            var currentTime = DateTime.Now.Ticks;
            if (_lastUserInputTime == 0)
            {
                long timerLastChanged = currentTime;
                var timer = new System.Windows.Forms.Timer { Interval = 250 };
                timer.Tick += (s, a) =>
                {
                    if (NoUserInput(timerLastChanged))
                    {
                        _ToolTipText = "";
                        var fileCount = 0;
                        try
                        {
                            fileCount = FilterFiles(FilterComboBox.Text);
                        }
                        catch (ArgumentException ae)
                        {
                            _ToolTipText = ae.Message;
                        }
                        if (fileCount > 0)
                        {
                            AddToSelectionFilter(FilterComboBox.Text);
                        }

                        timer.Stop();
                        _lastUserInputTime = 0;
                    }
                    timerLastChanged = _lastUserInputTime;
                };

                timer.Start();
            }

            _lastUserInputTime = currentTime;
        }

        private bool NoUserInput(long timerLastChanged)
        {
            return timerLastChanged == _lastUserInputTime;
        }

        private void AddToSelectionFilter(string filter)
        {
            if (!FilterComboBox.Items.Cast<string>().Any(candiate => candiate == filter))
            {
                const int SelectionFilterMaxLength = 10;
                if (FilterComboBox.Items.Count == SelectionFilterMaxLength)
                {
                    FilterComboBox.Items.RemoveAt(SelectionFilterMaxLength - 1);
                }
                FilterComboBox.Items.Insert(0, filter);
            }
        }

        private void FilterComboBox_MouseEnter(object sender, EventArgs e)
        {
            FilterToolTip.SetToolTip(FilterComboBox, _ToolTipText);
        }

        private void FilterComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            FilterFiles(FilterComboBox.Text);
        }

        private void FilterComboBox_GotFocus(object sender, EventArgs e)
        {
            FilterWatermarkLabel.Visible = false;
        }

        private void FilterComboBox_LostFocus(object sender, EventArgs e)
        {
            if (!FilterWatermarkLabel.Visible && string.IsNullOrEmpty(FilterComboBox.Text))
            {
                FilterWatermarkLabel.Visible = true;
            }
        }

        private Regex _filter;

        #endregion Filtering
    }

}
