﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ErrorManager;
using GRF;
using GRF.Core;
using GRF.Core.GroupedGrf;
using GRF.FileFormats.GndFormat;
using GRF.FileFormats.RsmFormat;
using GRF.FileFormats.RswFormat;
using GRF.FileFormats.StrFormat;
using GRF.IO;
using GRF.Threading;
using GRFEditor.ApplicationConfiguration;
using GRFEditor.Core;
using GRFEditor.Core.Services;
using GrfToWpfBridge.Application;
using TokeiLibrary;
using Utilities;
using Utilities.Extension;
using Configuration = GRFEditor.ApplicationConfiguration.GrfEditorConfiguration;
using OpeningService = Utilities.Services.OpeningService;

namespace GRFEditor.Tools.MapExtractor {
	/// <summary>
	/// Interaction logic for MapExtractor.xaml
	/// </summary>
	public partial class MapExtractor : UserControl, IProgress, IDisposable {
		private readonly AsyncOperation _asyncOperation;
		private readonly object _lock = new object();
		private readonly MultiGrfReader _metaGrf;
		private string _destinationPath;
		private string _fileName;
		private GrfHolder _grf;
		private string _grfPath;
		private bool _requiresMetaGrfReload = true;

		public MapExtractor(GrfHolder grf, string fileName) {
			_grfPath = Path.GetDirectoryName(fileName);
			_grf = grf;
			_fileName = fileName;
			//_metaGrf = metaGrf;
			_metaGrf = new MultiGrfReader();

			InitializeComponent();

			_treeViewMapExtractor.SelectedItemChanged += new RoutedPropertyChangedEventHandler<object>(_treeViewMapExtractor_SelectedItemChanged);

			_treeViewMapExtractor.DoDragDropCustomMethod = delegate {
				VirtualFileDataObjectProgress vfop = new VirtualFileDataObjectProgress();
				VirtualFileDataObject virtualFileDataObject = new VirtualFileDataObject(
					_ => _asyncOperation.SetAndRunOperation(new GrfThread(vfop.Update, vfop, 500, null)),
					_ => vfop.Finished = true
					);

				IEnumerable<MapExtractorTreeViewItem> allNodes = _treeViewMapExtractor.SelectedItems.Items.Cast<MapExtractorTreeViewItem>().Where(p => p.ResourcePath != null);

				List<VirtualFileDataObject.FileDescriptor> descriptors = allNodes.Select(node => new VirtualFileDataObject.FileDescriptor {
					Name = Path.GetFileName(node.ResourcePath.RelativePath),
					FilePath = node.ResourcePath.RelativePath,
					Argument = _metaGrf,
					StreamContents = (grfData, filePath, stream, argument) => {
						MultiGrfReader metaGrfArg = (MultiGrfReader) argument;

						var data = metaGrfArg.GetData(filePath);
						stream.Write(data, 0, data.Length);
						vfop.ItemsProcessed++;
					}
				}).ToList();

				vfop.Vfo = virtualFileDataObject;
				vfop.ItemsToProcess = descriptors.Count;
				virtualFileDataObject.Source = DragAndDropSource.ResourceExtractor;
				virtualFileDataObject.SetData(descriptors);

				try {
					VirtualFileDataObject.DoDragDrop(_treeViewMapExtractor, virtualFileDataObject, DragDropEffects.Copy);
				}
				catch (Exception err) {
					ErrorHandler.HandleException(err);
				}
			};

			_asyncOperation = new AsyncOperation(_progressBarComponent);
			_quickPreview.Set(_asyncOperation, _metaGrf);

			_itemsResources2.SaveResourceMethod = v => Configuration.MapExtractorResources = v;
			_itemsResources2.LoadResourceMethod = () => {
				var items = Methods.StringToList(Configuration.MapExtractorResources);

				if (!items.Contains(GrfStrings.CurrentlyOpenedGrf + grf.FileName)) {
					items.RemoveAll(p => p.StartsWith(GrfStrings.CurrentlyOpenedGrf));
					items.Insert(0, GrfStrings.CurrentlyOpenedGrf + grf.FileName);
				}

				return items;
			};
			_itemsResources2.Modified += delegate {
				_requiresMetaGrfReload = true;
				_asyncOperation.SetAndRunOperation(new GrfThread(() => _updateMapFiles(_fileName, null), this, 200, null, false, true));
			};
			_itemsResources2.LoadResourcesInfo();
			_itemsResources2.CanDeleteMainGrf = false;
		}

		public AsyncOperation AsyncOperation {
			get { return _asyncOperation; }
		}

		#region IDisposable Members

		public void Dispose() {
			Dispose(true);
			GC.SuppressFinalize(this);
		}

		#endregion

		#region IProgress Members

		public float Progress { get; set; }
		public bool IsCancelling { get; set; }
		public bool IsCancelled { get; set; }

		#endregion

		private void _treeViewMapExtractor_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) {
			try {
				MapExtractorTreeViewItem item = e.NewValue as MapExtractorTreeViewItem;

				if (item != null && item.ResourcePath != null) {
					_quickPreview.Update(item.ResourcePath.GetMostRelative());
				}
				else {
					_quickPreview.ClearPreview();
				}
			}
			catch (Exception err) {
				ErrorHandler.HandleException(err);
			}
		}

		public void Export() {
			_destinationPath = Configuration.OverrideExtractionPath ? Configuration.DefaultExtractingPath : Path.GetDirectoryName(new FileInfo(_grf.FileName).FullName);
			_asyncOperation.SetAndRunOperation(new GrfThread(_export, this, 200), _openFolderCallback);
		}

		public void ExportAt() {
			string path = PathRequest.FolderExtract();

			if (path != null) {
				_destinationPath = path;
				_asyncOperation.SetAndRunOperation(new GrfThread(_export, this, 200), _openFolderCallback);
			}
		}

		public void Reload(GrfHolder grf, string fileName, Func<bool> cancelMethod) {
			if (_asyncOperation.IsRunning)
				return;

			_grfPath = Path.GetDirectoryName(fileName);
			_grf = grf;
			_fileName = fileName;

			new Thread(() => _updateMapFiles(fileName, cancelMethod)) { Name = "GrfEditor - MapExtractor map update thread" }.Start();
		}

		private void _disableNode(MapExtractorTreeViewItem gndTextureNode) {
			gndTextureNode.Dispatcher.Invoke(new Action(delegate {
				gndTextureNode.CheckBoxHeader.IsEnabled = false;
				gndTextureNode.TextBlock.Foreground = new SolidColorBrush(Colors.Red);
				gndTextureNode.ResourcePath = null;
			}));
		}

		private void _checkParents(MapExtractorTreeViewItem item, bool value) {
			try {
				if (item.Parent != null) {
					MapExtractorTreeViewItem parent = item.Parent as MapExtractorTreeViewItem;

					if (parent != null) {
						bool allChildrenEqualValue = true;

						foreach (MapExtractorTreeViewItem child in parent.Items) {
							if (child.CheckBoxHeader.IsEnabled && child.CheckBoxHeader.IsChecked != value) {
								allChildrenEqualValue = false;
								break;
							}
						}

						if (allChildrenEqualValue) {
							parent.CheckBoxHeader.IsChecked = value;
							_checkParents(parent, value);
						}
						else if (parent.CheckBoxHeader.IsChecked != value) {
							parent.CheckBoxHeader.IsChecked = null;

							_checkParents(parent, value);
						}
					}
				}
			}
			catch (Exception err) {
				ErrorHandler.HandleException(err);
			}
		}

		private void _export() {
			try {
				Progress = -1;

				List<TokeiTuple<TkPath, string>> selectedNodes = _getSelectedNodes(null).GroupBy(p => p.Item1.GetFullPath()).Select(p => p.First()).ToList();
				List<string> pathsToCreate = selectedNodes.Select(p => Path.Combine(_destinationPath, Path.GetDirectoryName(p.Item2))).Distinct().ToList();

				foreach (string pathToCreate in pathsToCreate) {
					if (!Directory.Exists(pathToCreate))
						Directory.CreateDirectory(pathToCreate);
				}

				for (int index = 0; index < selectedNodes.Count; index++) {
					string relativePath = selectedNodes[index].Item2;

					string outputPath = Path.Combine(_destinationPath, relativePath);

					File.WriteAllBytes(outputPath, _metaGrf.GetData(relativePath));

					Progress = (float) (index + 1) / selectedNodes.Count * 100f;
				}

				// Find topmost directory
				if (pathsToCreate.Count > 0)
					_destinationPath = pathsToCreate[0];
			}
			catch (Exception err) {
				ErrorHandler.HandleException(err);
			}
			finally {
				Progress = 100f;
			}
		}

		private void _openFolderCallback(object state) {
			try {
				OpeningService.FileOrFolder(_destinationPath);
			}
			catch (Exception err) {
				ErrorHandler.HandleException(err);
			}
		}

		private void _updateMapFiles(string fileName, Func<bool> cancelMethod) {
			try {
				lock (_lock) {
					if (cancelMethod != null && cancelMethod()) return;

					string mapFile = Path.GetFileNameWithoutExtension(fileName);
					Progress = -1;

					_treeViewMapExtractor.Dispatch(p => p.Items.Clear());
					_quickPreview.ClearPreview();

					if (_requiresMetaGrfReload)
						_metaGrf.Update(_itemsResources2.Paths, _grf);

					_requiresMetaGrfReload = false;

					if (fileName.IsExtension(".rsm")) {
						if (cancelMethod != null && cancelMethod()) return;
						_addNode(cancelMethod, mapFile + ".rsm", _grfPath, null);
					}
					else if (fileName.IsExtension(".rsm2")) {
						if (cancelMethod != null && cancelMethod()) return;
						_addNode(cancelMethod, mapFile + ".rsm2", _grfPath, null);
					}
					else if (fileName.IsExtension(".str")) {
						if (cancelMethod != null && cancelMethod()) return;
						_addNode(cancelMethod, mapFile + ".str", _grfPath, null);
					}
					else {
						if (cancelMethod != null && cancelMethod()) return;
						_addNode(cancelMethod, mapFile + ".gnd", @"data\", null, fileName.IsExtension(".gnd"));

						if (cancelMethod != null && cancelMethod()) return;
						_treeViewMapExtractor.Dispatcher.Invoke(new Action(delegate {
							foreach (MapExtractorTreeViewItem node in _treeViewMapExtractor.Items) {
								if (node.CheckBoxHeader.IsChecked == true) {
									node.IsExpanded = true;
								}
							}
						}));

						_addNode(cancelMethod, mapFile + ".rsw", @"data\", null, fileName.IsExtension(".rsw"));
					}

					if (cancelMethod != null && cancelMethod()) return;
					_treeViewMapExtractor.Dispatcher.Invoke(new Action(delegate {
						foreach (MapExtractorTreeViewItem node in _treeViewMapExtractor.Items) {
							if (node.CheckBoxHeader.IsChecked == true) {
								node.IsExpanded = true;
							}
						}
					}));
				}
			}
			catch (Exception err) {
				_requiresMetaGrfReload = true;
				ErrorHandler.HandleException(err);
			}
			finally {
				Progress = 100;
			}
		}

		private void _addNode(Func<bool> cancelMethod, string subRelativeFile, string relativeResourceLocation, MapExtractorTreeViewItem parent, bool isChecked = true) {
			try {
				if (cancelMethod != null && cancelMethod()) return;

				MapExtractorTreeViewItem mainNode = (MapExtractorTreeViewItem)_treeViewMapExtractor.Dispatcher.Invoke(new Func<MapExtractorTreeViewItem>(() => new MapExtractorTreeViewItem(_treeViewMapExtractor)));

				mainNode.Dispatch(p => p.CheckBoxHeader.Checked += new RoutedEventHandler(_checkBox_Checked));
				mainNode.Dispatch(p => p.CheckBoxHeader.Unchecked += new RoutedEventHandler(_checkBox_Unchecked));
				mainNode.Dispatch(p => p.HeaderText = subRelativeFile);

				string relativePath = Path.Combine(relativeResourceLocation, subRelativeFile);

				if (_metaGrf.GetData(relativePath) == null) {
					mainNode.Dispatch(p => p.ResourcePath = null);
				}
				else {
					mainNode.Dispatch(p => p.ResourcePath = _metaGrf.FindTkPath(relativePath));
				}

				if (parent != null)
					parent.Dispatch(p => p.Items.Add(mainNode));
				else
					_treeViewMapExtractor.Dispatch(p => p.Items.Add(mainNode));

				if (mainNode.ResourcePath != null) {
					mainNode.Dispatch(p => p.CheckBoxHeader.IsChecked = isChecked);
					string extension = subRelativeFile.GetExtension();

					if (cancelMethod != null && cancelMethod()) return;

					switch (extension) {
						case ".rsm":
						case ".rsm2":
							Rsm rsm = new Rsm(_metaGrf.GetData(relativePath));
							HashSet<string> textures = new HashSet<string>();

							foreach (var mesh in rsm.Meshes) {
								foreach (var texture in mesh.Textures) {
									textures.Add(texture);
								}
							}

							foreach (var texture in rsm.Textures) {
								textures.Add(texture);
							}

							foreach (string texture in textures) {
								_addNode(cancelMethod, texture, @"data\texture\", mainNode, isChecked);
							}
							break;
						case ".gnd":
							Gnd gnd = new Gnd(_metaGrf.GetData(relativePath));

							foreach (string texture in gnd.TexturesPath.Distinct()) {
								_addNode(cancelMethod, texture, @"data\texture\", mainNode, isChecked);
							}
							break;
						case ".rsw":
							Rsw rsw = new Rsw(_metaGrf.GetData(relativePath));

							foreach (string model in rsw.ModelResources.Distinct()) {
								_addNode(cancelMethod, model, @"data\model\", mainNode, isChecked);
							}
							break;
						case ".str":
							Str str = new Str(_metaGrf.GetData(relativePath));

							List<string> resources = str.Layers.SelectMany(layer => layer.TextureNames).ToList();

							foreach (string resource in resources.Distinct()) {
								_addNode(cancelMethod, resource, relativeResourceLocation, mainNode, isChecked);
							}
							break;
					}
				}
				else {
					_disableNode(mainNode);
				}
			}
			catch (Exception err) {
				ErrorHandler.HandleException(err);
			}
		}

		private IEnumerable<TokeiTuple<TkPath, string>> _getSelectedNodes(MapExtractorTreeViewItem node) {
			return (List<TokeiTuple<TkPath, string>>) _treeViewMapExtractor.Dispatcher.Invoke(new Func<List<TokeiTuple<TkPath, string>>>(delegate {
				List<TokeiTuple<TkPath, string>> paths = new List<TokeiTuple<TkPath, string>>();

				if (node == null) {
					foreach (MapExtractorTreeViewItem mapNode in _treeViewMapExtractor.Items) {
						paths.AddRange(_getSelectedNodes(mapNode));
					}
				}
				else {
					if (node.CheckBoxHeader.IsChecked == true) {
						paths.Add(new TokeiTuple<TkPath, string>(node.ResourcePath, node.ResourcePath.RelativePath));
					}

					foreach (MapExtractorTreeViewItem mapNode in node.Items) {
						paths.AddRange(_getSelectedNodes(mapNode));
					}
				}

				return paths;
			}));
		}

		private void _menuItemsSelectInGrf_Click(object sender, RoutedEventArgs e) {
			try {
				TkPath path = ((MapExtractorTreeViewItem) _treeViewMapExtractor.SelectedItem).ResourcePath;

				if (path == null) {
					ErrorHandler.HandleException("This file isn't present in the currently opened GRF.", ErrorLevel.Low);
					return;
				}

				if (_grf.FileName == path.FilePath)
					PreviewService.Select(null, null, path.RelativePath);
				else
					ErrorHandler.HandleException("This file isn't present in the currently opened GRF.", ErrorLevel.Low);
			}
			catch (Exception err) {
				ErrorHandler.HandleException(err);
			}
		}

		protected virtual void Dispose(bool disposing) {
			if (disposing) {
				if (_metaGrf != null) {
					_metaGrf.Dispose();
				}
			}
		}

		#region Events

		private void _checkBox_Checked(object sender, RoutedEventArgs e) {
			try {
				MapExtractorTreeViewItem item = WpfUtilities.FindParentControl<MapExtractorTreeViewItem>(sender as DependencyObject);

				if (item != null) {
					foreach (MapExtractorTreeViewItem tvi in item.Items) {
						if (tvi.CheckBoxHeader.IsEnabled)
							tvi.CheckBoxHeader.IsChecked = true;
					}

					_checkParents(item, true);
				}
			}
			catch (Exception err) {
				ErrorHandler.HandleException(err);
			}
		}

		private void _checkBox_Unchecked(object sender, RoutedEventArgs e) {
			try {
				MapExtractorTreeViewItem item = WpfUtilities.FindParentControl<MapExtractorTreeViewItem>(sender as DependencyObject);

				if (item != null) {
					foreach (MapExtractorTreeViewItem tvi in item.Items) {
						if (tvi.CheckBoxHeader.IsEnabled)
							tvi.CheckBoxHeader.IsChecked = false;
					}

					_checkParents(item, false);
				}
			}
			catch (Exception err) {
				ErrorHandler.HandleException(err);
			}
		}

		private void _menuItemsSelectRootFiles_Click(object sender, RoutedEventArgs e) {
			if (_treeViewMapExtractor.SelectedItem != null) {
				MapExtractorTreeViewItem tvi = (MapExtractorTreeViewItem) _treeViewMapExtractor.SelectedItem;

				if (tvi.ResourcePath != null) {
					tvi.CheckBoxHeader.Checked -= _checkBox_Checked;
					tvi.CheckBoxHeader.IsChecked = true;
					tvi.CheckBoxHeader.Checked += _checkBox_Checked;
				}

				foreach (MapExtractorTreeViewItem child in tvi.Items) {
					if (child.ResourcePath == null)
						continue;

					child.CheckBoxHeader.Checked -= _checkBox_Checked;

					bool allChecked = true;

					foreach (MapExtractorTreeViewItem subChild in child.Items) {
						if (subChild.ResourcePath == null)
							continue;

						if (subChild.CheckBoxHeader.IsChecked != true) {
							allChecked = false;
						}
					}

					if (!allChecked) {
						child.CheckBoxHeader.IsChecked = null;

						tvi.CheckBoxHeader.Checked -= _checkBox_Checked;
						tvi.CheckBoxHeader.IsChecked = null;
						tvi.CheckBoxHeader.Checked += _checkBox_Checked;
					}
					else {
						child.CheckBoxHeader.IsChecked = true;
					}

					child.CheckBoxHeader.Checked += _checkBox_Checked;
				}

				if (tvi.CheckBoxHeader.IsChecked == true) {
					_checkParents(tvi, true);
				}
			}
		}

		private void _treeViewMapExtractor_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e) {
			TreeViewItem item = WpfUtilities.GetTreeViewItemClicked((FrameworkElement) e.OriginalSource, _treeViewMapExtractor);

			if (item != null) {
				item.IsSelected = true;
				_treeViewMapExtractor.ContextMenu.IsOpen = true;
				e.Handled = false;
			}
			else {
				_treeViewMapExtractor.ContextMenu.IsOpen = false;
				e.Handled = true;
			}
		}

		#endregion

		private void _menuItemsSelectInExplorer_Click(object sender, RoutedEventArgs e) {
			try {
				TkPath path = ((MapExtractorTreeViewItem)_treeViewMapExtractor.SelectedItem).ResourcePath;

				if (path == null) {
					ErrorHandler.HandleException("This file isn't present in the currently opened GRF.", ErrorLevel.Low);
					return;
				}

				var destinationPath = GrfPath.Combine(Configuration.OverrideExtractionPath ? Configuration.DefaultExtractingPath : Path.GetDirectoryName(new FileInfo(_grf.FileName).FullName), path.RelativePath);

				OpeningService.FileOrFolder(destinationPath);
			}
			catch (Exception err) {
				ErrorHandler.HandleException(err);
			}
		}
	}
}