using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Xml;
using System.Windows.Documents;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace StyleSnooper
{
    public partial class MainWindow : INotifyPropertyChanged
    {
        private readonly Style _bracketStyle, _elementStyle, _quotesStyle, _textStyle, _attributeStyle;
        private readonly Microsoft.Win32.OpenFileDialog _openFileDialog;

        public MainWindow()
        {
            ElementTypes = GetFrameworkElementTypesFromAssembly(typeof(FrameworkElement).Assembly);

            InitializeComponent();

            // get syntax coloring styles
            _bracketStyle   = (Style)Resources["BracketStyle"];
            _elementStyle   = (Style)Resources["ElementStyle"];
            _quotesStyle    = (Style)Resources["QuotesStyle"];
            _textStyle      = (Style)Resources["TextStyle"];
            _attributeStyle = (Style)Resources["AttributeStyle"];

            // start out by looking at Button
            CollectionViewSource.GetDefaultView(ElementTypes).MoveCurrentTo(typeof(Button));

            // create the file open dialog
            _openFileDialog = new Microsoft.Win32.OpenFileDialog
            {
                CheckFileExists = true,
                Multiselect = false,
                Filter = "Assemblies (*.exe;*.dll)|*.exe;*.dll"
            };
        }

        public Type[] ElementTypes { get; private set; }

        private static Type[] GetFrameworkElementTypesFromAssembly(Assembly assembly)
        {
            // create a list of all types in PresentationFramework that are non-abstract,
            // and non-generic, derive from FrameworkElement, and have a default constructor
            var typeList = new List<Type>();
            foreach (var type in assembly.GetTypes())
            {
                if (// type.IsPublic && // maybe we wanna peek at nonpublic ones?
                    !type.IsAbstract &&
                    !type.ContainsGenericParameters &&
                    typeof(FrameworkElement).IsAssignableFrom(type) &&
                    (type.GetConstructor(Type.EmptyTypes) != null ||
                        type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null) != null // allow a nonpublic ctor
                    ))
                {
                    typeList.Add(type);
                }
            }

            // sort the types by name
            typeList.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            
            return typeList.ToArray();
        }

        private void OnLoadClick(object sender, RoutedEventArgs e)
        {
            if (_openFileDialog.ShowDialog(this) != true)
                return;

            try
            {
                AsmName.Text = _openFileDialog.FileName;
                var types = GetFrameworkElementTypesFromAssembly(Assembly.LoadFile(_openFileDialog.FileName));
                if (types.Length == 0)
                {
                    MessageBox.Show("Assembly does not contain any compatible types.");
                }
                else
                {
                    ElementTypes = types;
                    OnPropertyChanged(nameof(ElementTypes));
                }
            }
            catch
            {
                MessageBox.Show("Error loading assembly.");
            }
        }

        private void ShowStyle(object sender, SelectionChangedEventArgs e)
        {
            if (styleTextBox == null) return;

            // see which type is selected
            var type = typeComboBox.SelectedValue as Type;
            if (type != null)
            {
                string serializedStyle;
                var success = TrySerializeStyle(type, out serializedStyle);

                // show the style in a document viewer
                styleTextBox.Document = CreateFlowDocument(success, serializedStyle);
            }
        }

        /// <summary>
        /// Serializes a style using XamlWriter.
        /// </summary>
        /// <param name="type"></param>
        /// <param name="serializedStyle"></param>
        /// <returns></returns>
        private static bool TrySerializeStyle(Type type, out string serializedStyle)
        {
            var success = false;
            serializedStyle = "[Style not found]";
            
            // make an instance of the type and get its default style key
            var nonPublic = type.GetConstructor(Type.EmptyTypes) == null;
            var element = (FrameworkElement)Activator.CreateInstance(type, nonPublic);

            var defaultStyleKey = element.GetValue(DefaultStyleKeyProperty);
            
            if (defaultStyleKey != null)
            {
                // try to get the default style for the type
                var style = Application.Current.TryFindResource(defaultStyleKey) as Style;
                if (style != null)
                {
                    // try to serialize the style
                    try
                    {
                        var stringWriter = new StringWriter();
                        var xmlTextWriter = new XmlTextWriter(stringWriter) {Formatting = Formatting.Indented};
                        System.Windows.Markup.XamlWriter.Save(style, xmlTextWriter);
                        serializedStyle = stringWriter.ToString();

                        success = true;
                    }
                    catch (Exception exception)
                    {
                        serializedStyle = "[Exception thrown while serializing style]" +
                            Environment.NewLine + Environment.NewLine + exception;
                    }
                }
            }
            return success;
        }

        /// <summary>
        /// Creates a FlowDocument from the serialized XAML with simple syntax coloring.
        /// </summary>
        /// <param name="success"></param>
        /// <param name="serializedStyle"></param>
        /// <returns></returns>
        private FlowDocument CreateFlowDocument(bool success, string serializedStyle)
        {
            var document = new FlowDocument();
            if (success)
            {
                using (var reader = new XmlTextReader(serializedStyle, XmlNodeType.Document, null))
                {
                    var indent = 0;
                    var paragraph = new Paragraph();
                    while (reader.Read())
                    {
                        if (reader.IsStartElement()) // opening tag, e.g. <Button
                        {
                            // indentation
                            AddRun(paragraph, _textStyle, new string(' ', indent * 4));

                            AddRun(paragraph, _bracketStyle, "<");
                            AddRun(paragraph, _elementStyle, reader.Name);
                            if (reader.HasAttributes)
                            {
                                // write tag attributes
                                while (reader.MoveToNextAttribute())
                                {
                                    AddRun(paragraph, _attributeStyle, " " + reader.Name);
                                    AddRun(paragraph, _bracketStyle, "=");
                                    AddRun(paragraph, _quotesStyle, "\"");
                                    if (reader.Name == "TargetType") // target type fix - should use the Type MarkupExtension
                                    {
                                        AddRun(paragraph, _textStyle, "{x:Type " + reader.Value + "}");
                                    }
                                    else
                                    {
                                        AddRun(paragraph, _textStyle, reader.Value);
                                    }
                                    AddRun(paragraph, _quotesStyle, "\"");
                                }
                                reader.MoveToElement();
                            }
                            if (reader.IsEmptyElement) // empty tag, e.g. <Button />
                            {
                                AddRun(paragraph, _bracketStyle, " />");
                                paragraph.Inlines.Add(new LineBreak());
                                --indent;
                            }
                            else // non-empty tag, e.g. <Button>
                            {
                                AddRun(paragraph, _bracketStyle, ">");
                                paragraph.Inlines.Add(new LineBreak());
                            }

                            ++indent;
                        }
                        else // closing tag, e.g. </Button>
                        {
                            --indent;

                            // indentation
                            AddRun(paragraph, _textStyle, new string(' ', indent * 4));

                            // text content of a tag, e.g. the text "Do This" in <Button>Do This</Button>
                            if (reader.NodeType == XmlNodeType.Text)
                            {
                                AddRun(paragraph, _textStyle, reader.ReadContentAsString());
                            }

                            AddRun(paragraph, _bracketStyle, "</");
                            AddRun(paragraph, _elementStyle, reader.Name);
                            AddRun(paragraph, _bracketStyle, ">");
                            paragraph.Inlines.Add(new LineBreak());
                        }
                    }
                    document.Blocks.Add(paragraph);
                }
            }
            else // no style found
            {
                document.Blocks.Add(new Paragraph(new Run(serializedStyle)) {TextAlignment = TextAlignment.Left});
            }
            return document;
        }

        /// <summary>
        /// Adds a span with the specified text and style.
        /// </summary>
        /// <param name="par"></param>
        /// <param name="style"></param>
        /// <param name="s"></param>
        private static void AddRun(Paragraph par, Style style, string s)
        {
            par.Inlines.Add(new Run(s) {Style = style});
        }

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }
}