// (c) Copyright Microsoft Corporation.
// This source is subject to the Microsoft Public License (Ms-PL).
// Please see http://go.microsoft.com/fwlink/?LinkID=131993] for details.
// All other rights reserved.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace System.Windows.Controls
{
    /// <summary>
    /// Represents a control that provides a text box for user input and a
    /// drop-down that contains possible matches based on the input in the text
    /// box.
    /// </summary>
    /// <QualityBand>Stable</QualityBand>
    [TemplatePart(Name = MultiselectAutoCompleteBox.ElementSelectionAdapter, Type = typeof(ISelectionAdapter))]
    [TemplatePart(Name = MultiselectAutoCompleteBox.ElementSelector, Type = typeof(Selector))]
    [TemplatePart(Name = MultiselectAutoCompleteBox.ElementTextBox, Type = typeof(TextBox))]
    [TemplatePart(Name = MultiselectAutoCompleteBox.ElementPopup, Type = typeof(Popup))]
    [StyleTypedProperty(Property = MultiselectAutoCompleteBox.ElementTextBoxStyle, StyleTargetType = typeof(TextBox))]
    [StyleTypedProperty(Property = MultiselectAutoCompleteBox.ElementItemContainerStyle, StyleTargetType = typeof(ListBox))]
    [TemplateVisualState(Name = VisualStates.StateNormal, GroupName = VisualStates.GroupCommon)]
    [TemplateVisualState(Name = VisualStates.StateMouseOver, GroupName = VisualStates.GroupCommon)]
    [TemplateVisualState(Name = VisualStates.StatePressed, GroupName = VisualStates.GroupCommon)]
    [TemplateVisualState(Name = VisualStates.StateDisabled, GroupName = VisualStates.GroupCommon)]
    [TemplateVisualState(Name = VisualStates.StateFocused, GroupName = VisualStates.GroupFocus)]
    [TemplateVisualState(Name = VisualStates.StateUnfocused, GroupName = VisualStates.GroupFocus)]
    [TemplateVisualState(Name = VisualStates.StatePopupClosed, GroupName = VisualStates.GroupPopup)]
    [TemplateVisualState(Name = VisualStates.StatePopupOpened, GroupName = VisualStates.GroupPopup)]
    [TemplateVisualState(Name = VisualStates.StateValid, GroupName = VisualStates.GroupValidation)]
    [TemplateVisualState(Name = VisualStates.StateInvalidFocused, GroupName = VisualStates.GroupValidation)]
    [TemplateVisualState(Name = VisualStates.StateInvalidUnfocused, GroupName = VisualStates.GroupValidation)]
    [SuppressMessage("Microsoft.Maintainability", "CA1506:AvoidExcessiveClassCoupling", Justification = "Large implementation keeps the components contained.")]
    [ContentProperty("ItemsSource")]
    public partial class MultiselectAutoCompleteBox : Control, IUpdateVisualState
    {
        #region Custom datasource implementation
        public class ProvideDataSourceEventArgs : EventArgs
        {
            public List<object> DataSource { get; set; }
            public string SearchString { get; set; }
        }

        public event EventHandler<ProvideDataSourceEventArgs> ProvideDataSource;
        #endregion

        #region ItemsPopulateComplete

        public event EventHandler<EventArgs> ItemsPopulateComplete;

        #endregion


        #region Template part and style names

        /// <summary>
        /// Specifies the name of the selection adapter TemplatePart.
        /// </summary>
        private const string ElementSelectionAdapter = "SelectionAdapter";

        /// <summary>
        /// Specifies the name of the Selector TemplatePart.
        /// </summary>
        private const string ElementSelector = "Selector";

        /// <summary>
        /// Specifies the name of the Popup TemplatePart.
        /// </summary>
        private const string ElementPopup = "Popup";
        
        /// <summary>
        /// The name for the text box part.
        /// </summary>
        private const string ElementTextBox = "Text";

        /// <summary>
        /// The name for the text box style.
        /// </summary>
        private const string ElementTextBoxStyle = "TextBoxStyle";

        /// <summary>
        /// The name for the adapter's item container style.
        /// </summary>
        private const string ElementItemContainerStyle = "ItemContainerStyle";

        #endregion

        /// <summary>
        /// Gets or sets a local cached copy of the items data.
        /// </summary>
        private List<object> _items;

        /// <summary>
        /// Gets or sets the observable collection that contains references to 
        /// all of the items in the generated view of data that is provided to 
        /// the selection-style control adapter.
        /// </summary>
        private ObservableCollection<object> _view;

        /// <summary>
        /// Gets or sets a value to ignore a number of pending change handlers. 
        /// The value is decremented after each use. This is used to reset the 
        /// value of properties without performing any of the actions in their 
        /// change handlers.
        /// </summary>
        /// <remarks>The int is important as a value because the TextBox 
        /// TextChanged event does not immediately fire, and this will allow for
        /// nested property changes to be ignored.</remarks>
        private int _ignoreTextPropertyChange;

        /// <summary>
        /// Gets or sets a value indicating whether to ignore calling a pending 
        /// change handlers. 
        /// </summary>
        private bool _ignorePropertyChange;

        /// <summary>
        /// Gets or sets a value indicating whether to ignore the selection 
        /// changed event.
        /// </summary>
        private bool _ignoreTextSelectionChange;

        /// <summary>
        /// Gets or sets a value indicating whether to skip the text update 
        /// processing when the selected item is updated.
        /// </summary>
        private bool _skipSelectedItemTextUpdate;

        /// <summary>
        /// Gets or sets the last observed text box selection start location.
        /// </summary>
        private int _textSelectionStart;

        /// <summary>
        /// Gets or sets a value indicating whether the user initiated the 
        /// current populate call.
        /// </summary>
        private bool _userCalledPopulate;

        /// <summary>
        /// A value indicating whether the popup has been opened at least once.
        /// </summary>
        private bool _popupHasOpened;

        /// <summary>
        /// Gets or sets the DispatcherTimer used for the MinimumPopulateDelay 
        /// condition for auto completion.
        /// </summary>
        private DispatcherTimer _delayTimer;

        /// <summary>
        /// Gets or sets a value indicating whether a read-only dependency 
        /// property change handler should allow the value to be set.  This is 
        /// used to ensure that read-only properties cannot be changed via 
        /// SetValue, etc.
        /// </summary>
        private bool _allowWrite;

        /// <summary>
        /// Gets or sets the helper that provides all of the standard
        /// interaction functionality. Making it internal for subclass access.
        /// </summary>
        internal InteractionHelper Interaction { get; set; }

        /// <summary>
        /// Gets or sets the BindingEvaluator, a framework element that can
        /// provide updated string values from a single binding.
        /// </summary>
        private BindingEvaluator<string> _valueBindingEvaluator;

        #region (Eugene) IsMultiSelect support

        private bool IsEntryStarted
        {
            get; set;
        }

        public bool IsMultiSelect
        {
            get; set;
        }

        public string SelectedText
        {
            get
            {
                if (TextBox != null)
                    return TextBox.SelectedText;
                else
                {
                    return String.Empty;
                }
            }
        }
        
        private readonly string[] delimeters = { ".", ",", ", ", ". ", " ", "\n", "\r" };
        private bool EndsWith(string str, int position)
        {
            string value = str.Substring(0, position);
            for (int i=0; i<delimeters.Length; i++)
            {
                if (value.EndsWith(delimeters[i]))
                    return true;
            }

            return false;
        }

        private readonly char[] blanks = {' ', '.', ',', '\r', '\n', '\t', ';', ':', '-', '/', '\'', '(', ')'};
        private bool IsBlankChar(char c)
        {
            for (int i=0; i<blanks.Length; i++)
            {
                if (c == blanks[i])
                {
                    return true;
                }                        
            }

            return false;
        }

        private int EntryStartingPosition = 0;
        protected bool IsNewEntryStarts()
        {
            string currentText = TextBox.Text;
            int cursorPosition = TextBox.SelectionStart;

            if (cursorPosition <= 1)
            {
                return true;
            }

            if (EndsWith(currentText, cursorPosition))
            {
                return true;
            }

            return false;
        }

        private bool detectPrefixes;
        public bool DetectPrefixes
        {
            get
            {
                return detectPrefixes;
            }

            set
            {
                detectPrefixes = value;
            }
        }
        #endregion


        /// <summary>
        /// A weak event listener for the collection changed event.
        /// </summary>
        private WeakEventListener<MultiselectAutoCompleteBox, object, NotifyCollectionChangedEventArgs> _collectionChangedWeakEventListener;

        #region public int MinimumPrefixLength
        /// <summary>
        /// Gets or sets the minimum number of characters required to be entered
        /// in the text box before the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> displays
        /// possible matches.
        /// matches.
        /// </summary>
        /// <value>
        /// The minimum number of characters to be entered in the text box
        /// before the <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" />
        /// displays possible matches. The default is 1.
        /// </value>
        /// <remarks>
        /// If you set MinimumPrefixLength to -1, the MultiselectAutoCompleteBox will
        /// not provide possible matches. There is no maximum value, but
        /// setting MinimumPrefixLength to value that is too large will
        /// prevent the MultiselectAutoCompleteBox from providing possible matches as well.
        /// </remarks>
        public int MinimumPrefixLength
        {
            get { return (int)GetValue(MinimumPrefixLengthProperty); }
            set { SetValue(MinimumPrefixLengthProperty, value); }
        }

        /// <summary>
        /// Identifies the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.MinimumPrefixLength" />
        /// dependency property.
        /// </summary>
        /// <value>The identifier for the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.MinimumPrefixLength" />
        /// dependency property.</value>
        public static readonly DependencyProperty MinimumPrefixLengthProperty =
            DependencyProperty.Register(
                "MinimumPrefixLength",
                typeof(int),
                typeof(MultiselectAutoCompleteBox),
                new PropertyMetadata(1, OnMinimumPrefixLengthPropertyChanged));

        /// <summary>
        /// MinimumPrefixLengthProperty property changed handler.
        /// </summary>
        /// <param name="d">MultiselectAutoCompleteBox that changed its MinimumPrefixLength.</param>
        /// <param name="e">Event arguments.</param>
        [SuppressMessage("Microsoft.Usage", "CA2208:InstantiateArgumentExceptionsCorrectly", Justification = "MinimumPrefixLength is the name of the actual dependency property.")]
        private static void OnMinimumPrefixLengthPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            int newValue = (int)e.NewValue;

            if (newValue < 0 && newValue != -1)
            {
                throw new ArgumentOutOfRangeException("MinimumPrefixLength");
            }
        }
        #endregion public int MinimumPrefixLength

        #region public int MinimumPopulateDelay
        /// <summary>
        /// Gets or sets the minimum delay, in milliseconds, after text is typed
        /// in the text box before the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> control
        /// populates the list of possible matches in the drop-down.
        /// </summary>
        /// <value>The minimum delay, in milliseconds, after text is typed in
        /// the text box, but before the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> populates
        /// the list of possible matches in the drop-down. The default is 0.</value>
        /// <exception cref="T:System.ArgumentException">The set value is less than 0.</exception>
        public int MinimumPopulateDelay
        {
            get { return (int)GetValue(MinimumPopulateDelayProperty); }
            set { SetValue(MinimumPopulateDelayProperty, value); }
        }

        /// <summary>
        /// Identifies the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.MinimumPopulateDelay" />
        /// dependency property.
        /// </summary>
        /// <value>The identifier for the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.MinimumPopulateDelay" />
        /// dependency property.</value>
        public static readonly DependencyProperty MinimumPopulateDelayProperty =
            DependencyProperty.Register(
                "MinimumPopulateDelay",
                typeof(int),
                typeof(MultiselectAutoCompleteBox),
                new PropertyMetadata(OnMinimumPopulateDelayPropertyChanged));

        /// <summary>
        /// MinimumPopulateDelayProperty property changed handler. Any current 
        /// dispatcher timer will be stopped. The timer will not be restarted 
        /// until the next TextUpdate call by the user.
        /// </summary>
        /// <param name="d">AutoCompleteTextBox that changed its 
        /// MinimumPopulateDelay.</param>
        /// <param name="e">Event arguments.</param>
        [SuppressMessage("Microsoft.Usage", "CA2208:InstantiateArgumentExceptionsCorrectly", Justification = "The exception is most likely to be called through the CLR property setter.")]
        private static void OnMinimumPopulateDelayPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            MultiselectAutoCompleteBox source = d as MultiselectAutoCompleteBox;

            if (source._ignorePropertyChange)
            {
                source._ignorePropertyChange = false;
                return;
            }

            int newValue = (int)e.NewValue;
            if (newValue < 0)
            {
                source._ignorePropertyChange = true;
                d.SetValue(e.Property, e.OldValue);

                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, Properties.Resources.AutoComplete_OnMinimumPopulateDelayPropertyChanged_InvalidValue, newValue), "value");
            }

            // Stop any existing timer
            if (source._delayTimer != null)
            {
                source._delayTimer.Stop();
                
                if (newValue == 0)
                {
                    source._delayTimer = null;
                }
            }

            // Create or clear a dispatcher timer instance
            if (newValue > 0 && source._delayTimer == null)
            {
                source._delayTimer = new DispatcherTimer();
                source._delayTimer.Tick += source.PopulateDropDown;
            }

            // Set the new tick interval
            if (newValue > 0 && source._delayTimer != null)
            {
                source._delayTimer.Interval = TimeSpan.FromMilliseconds(newValue);
            }
        }
        #endregion public int MinimumPopulateDelay
        
        #region public bool IsTextCompletionEnabled
        /// <summary>
        /// Gets or sets a value indicating whether the first possible match
        /// found during the filtering process will be displayed automatically
        /// in the text box.
        /// </summary>
        /// <value>
        /// True if the first possible match found will be displayed
        /// automatically in the text box; otherwise, false. The default is
        /// false.
        /// </value>
        public bool IsTextCompletionEnabled
        {
            get { return (bool)GetValue(IsTextCompletionEnabledProperty); }
            set { SetValue(IsTextCompletionEnabledProperty, value); }
        }

        /// <summary>
        /// Identifies the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.IsTextCompletionEnabled" />
        /// dependency property.
        /// </summary>
        /// <value>The identifier for the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.IsTextCompletionEnabled" />
        /// dependency property.</value>
        public static readonly DependencyProperty IsTextCompletionEnabledProperty =
            DependencyProperty.Register(
                "IsTextCompletionEnabled",
                typeof(bool),
                typeof(MultiselectAutoCompleteBox),
                new PropertyMetadata(false, null));

        #endregion public bool IsTextCompletionEnabled

        #region public DataTemplate ItemTemplate
        /// <summary>
        /// Gets or sets the <see cref="T:System.Windows.DataTemplate" /> used
        /// to display each item in the drop-down portion of the control.
        /// </summary>
        /// <value>The <see cref="T:System.Windows.DataTemplate" /> used to
        /// display each item in the drop-down. The default is null.</value>
        /// <remarks>
        /// You use the ItemTemplate property to specify the visualization 
        /// of the data objects in the drop-down portion of the MultiselectAutoCompleteBox 
        /// control. If your MultiselectAutoCompleteBox is bound to a collection and you 
        /// do not provide specific display instructions by using a 
        /// DataTemplate, the resulting UI of each item is a string 
        /// representation of each object in the underlying collection. 
        /// </remarks>
        public DataTemplate ItemTemplate
        {
            get { return GetValue(ItemTemplateProperty) as DataTemplate; }
            set { SetValue(ItemTemplateProperty, value); }
        }

        /// <summary>
        /// Identifies the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemTemplate" />
        /// dependency property.
        /// </summary>
        /// <value>The identifier for the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemTemplate" />
        /// dependency property.</value>
        public static readonly DependencyProperty ItemTemplateProperty =
            DependencyProperty.Register(
                "ItemTemplate",
                typeof(DataTemplate),
                typeof(MultiselectAutoCompleteBox),
                new PropertyMetadata(null));

        #endregion public DataTemplate ItemTemplate

        #region public Style ItemContainerStyle
        /// <summary>
        /// Gets or sets the <see cref="T:System.Windows.Style" /> that is
        /// applied to the selection adapter contained in the drop-down portion
        /// of the <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" />
        /// control.
        /// </summary>
        /// <value>The <see cref="T:System.Windows.Style" /> applied to the
        /// selection adapter contained in the drop-down portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> control.
        /// The default is null.</value>
        /// <remarks>
        /// The default selection adapter contained in the drop-down is a 
        /// ListBox control. 
        /// </remarks>
        public Style ItemContainerStyle
        {
            get { return GetValue(ItemContainerStyleProperty) as Style; }
            set { SetValue(ItemContainerStyleProperty, value); }
        }

        /// <summary>
        /// Identifies the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemContainerStyle" />
        /// dependency property.
        /// </summary>
        /// <value>The identifier for the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemContainerStyle" />
        /// dependency property.</value>
        public static readonly DependencyProperty ItemContainerStyleProperty =
            DependencyProperty.Register(
                ElementItemContainerStyle,
                typeof(Style),
                typeof(MultiselectAutoCompleteBox),
                new PropertyMetadata(null, null));

        #endregion public Style ItemContainerStyle

        #region public Style TextBoxStyle
        /// <summary>
        /// Gets or sets the <see cref="T:System.Windows.Style" /> applied to
        /// the text box portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> control.
        /// </summary>
        /// <value>The <see cref="T:System.Windows.Style" /> applied to the text
        /// box portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> control.
        /// The default is null.</value>
        public Style TextBoxStyle
        {
            get { return GetValue(TextBoxStyleProperty) as Style; }
            set { SetValue(TextBoxStyleProperty, value); }
        }

        /// <summary>
        /// Identifies the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.TextBoxStyle" />
        /// dependency property.
        /// </summary>
        /// <value>The identifier for the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.TextBoxStyle" />
        /// dependency property.</value>
        public static readonly DependencyProperty TextBoxStyleProperty =
            DependencyProperty.Register(
                ElementTextBoxStyle,
                typeof(Style),
                typeof(MultiselectAutoCompleteBox),
                new PropertyMetadata(null));

        #endregion public Style TextBoxStyle

        #region public double MaxDropDownHeight
        /// <summary>
        /// Gets or sets the maximum height of the drop-down portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> control.
        /// </summary>
        /// <value>The maximum height of the drop-down portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> control.
        /// The default is <see cref="F:System.Double.PositiveInfinity" />.</value>
        /// <exception cref="T:System.ArgumentException">The specified value is less than 0.</exception>
        public double MaxDropDownHeight
        {
            get { return (double)GetValue(MaxDropDownHeightProperty); }
            set { SetValue(MaxDropDownHeightProperty, value); }
        }

        /// <summary>
        /// Identifies the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.MaxDropDownHeight" />
        /// dependency property.
        /// </summary>
        /// <value>The identifier for the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.MaxDropDownHeight" />
        /// dependency property.</value>
        public static readonly DependencyProperty MaxDropDownHeightProperty =
            DependencyProperty.Register(
                "MaxDropDownHeight",
                typeof(double),
                typeof(MultiselectAutoCompleteBox),
                new PropertyMetadata(double.PositiveInfinity, OnMaxDropDownHeightPropertyChanged));

        /// <summary>
        /// MaxDropDownHeightProperty property changed handler.
        /// </summary>
        /// <param name="d">AutoCompleteTextBox that changed its MaxDropDownHeight.</param>
        /// <param name="e">Event arguments.</param>
        [SuppressMessage("Microsoft.Usage", "CA2208:InstantiateArgumentExceptionsCorrectly", Justification = "The exception will be called through a CLR setter in most cases.")]
        private static void OnMaxDropDownHeightPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            MultiselectAutoCompleteBox source = d as MultiselectAutoCompleteBox;
            if (source._ignorePropertyChange)
            {
                source._ignorePropertyChange = false;
                return;
            }

            double newValue = (double)e.NewValue;
            
            // Revert to the old value if invalid (negative)
            if (newValue < 0)
            {
                source._ignorePropertyChange = true;
                source.SetValue(e.Property, e.OldValue);

                throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, Properties.Resources.AutoComplete_OnMaxDropDownHeightPropertyChanged_InvalidValue, e.NewValue), "value");
            }

            source.OnMaxDropDownHeightChanged(newValue);
        }
        #endregion public double MaxDropDownHeight

        #region public bool IsDropDownOpen
        /// <summary>
        /// Gets or sets a value indicating whether the drop-down portion of
        /// the control is open.
        /// </summary>
        /// <value>
        /// True if the drop-down is open; otherwise, false. The default is
        /// false.
        /// </value>
        public bool IsDropDownOpen
        {
            get { return (bool)GetValue(IsDropDownOpenProperty); }
            set { SetValue(IsDropDownOpenProperty, value); }
        }

        /// <summary>
        /// Identifies the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.IsDropDownOpen" />
        /// dependency property.
        /// </summary>
        /// <value>The identifier for the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.IsDropDownOpen" />
        /// dependency property.</value>
        public static readonly DependencyProperty IsDropDownOpenProperty =
            DependencyProperty.Register(
                "IsDropDownOpen",
                typeof(bool),
                typeof(MultiselectAutoCompleteBox),
                new PropertyMetadata(false, OnIsDropDownOpenPropertyChanged));

        /// <summary>
        /// IsDropDownOpenProperty property changed handler.
        /// </summary>
        /// <param name="d">AutoCompleteTextBox that changed its IsDropDownOpen.</param>
        /// <param name="e">Event arguments.</param>
        private static void OnIsDropDownOpenPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            MultiselectAutoCompleteBox source = d as MultiselectAutoCompleteBox;

            // Ignore the change if requested
            if (source._ignorePropertyChange)
            {
                source._ignorePropertyChange = false;
                return;
            }

            bool oldValue = (bool)e.OldValue;
            bool newValue = (bool)e.NewValue;

            if (newValue)
            {
                // source.TextUpdated(source.Text, true);
                source.ShowDropDown();
            }
            else
            {
                source.ClosingDropDown(oldValue);
            }

            source.UpdateVisualState(true);
        }
        #endregion public bool IsDropDownOpen

        #region public IEnumerable ItemsSource
        /// <summary>
        /// Gets or sets a collection that is used to generate the items for the
        /// drop-down portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> control.
        /// </summary>
        /// <value>The collection that is used to generate the items of the
        /// drop-down portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> control.</value>
        public IEnumerable ItemsSource
        {
            get { return GetValue(ItemsSourceProperty) as IEnumerable; }
            set { SetValue(ItemsSourceProperty, value); }
        }

        /// <summary>
        /// Identifies the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemsSource" />
        /// dependency property.
        /// </summary>
        /// <value>The identifier for the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemsSource" />
        /// dependency property.</value>
        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(
                "ItemsSource",
                typeof(IEnumerable),
                typeof(MultiselectAutoCompleteBox),
                new PropertyMetadata(OnItemsSourcePropertyChanged));

        /// <summary>
        /// ItemsSourceProperty property changed handler.
        /// </summary>
        /// <param name="d">MultiselectAutoCompleteBox that changed its ItemsSource.</param>
        /// <param name="e">Event arguments.</param>
        private static void OnItemsSourcePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            MultiselectAutoCompleteBox autoComplete = d as MultiselectAutoCompleteBox;
            autoComplete.OnItemsSourceChanged((IEnumerable)e.OldValue, (IEnumerable)e.NewValue);
        }

        #endregion public IEnumerable ItemsSource

        #region public object SelectedItem
        /// <summary>
        /// Gets or sets the selected item in the drop-down.
        /// </summary>
        /// <value>The selected item in the drop-down.</value>
        /// <remarks>
        /// If the IsTextCompletionEnabled property is true and text typed by 
        /// the user matches an item in the ItemsSource collection, which is 
        /// then displayed in the text box, the SelectedItem property will be 
        /// a null reference.
        /// </remarks>
        public object SelectedItem
        {
            get { return GetValue(SelectedItemProperty) as object; }
            set { SetValue(SelectedItemProperty, value); }
        }

        /// <summary>
        /// Identifies the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.SelectedItem" />
        /// dependency property.
        /// </summary>
        /// <value>The identifier the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.SelectedItem" />
        /// dependency property.</value>
        public static readonly DependencyProperty SelectedItemProperty =
            DependencyProperty.Register(
                "SelectedItem",
                typeof(object),
                typeof(MultiselectAutoCompleteBox),
                new PropertyMetadata(OnSelectedItemPropertyChanged));

        /// <summary>
        /// SelectedItemProperty property changed handler. Fires the 
        /// SelectionChanged event. The event data will contain any non-null
        /// removed items and non-null additions.
        /// </summary>
        /// <param name="d">MultiselectAutoCompleteBox that changed its SelectedItem.</param>
        /// <param name="e">Event arguments.</param>
        private static void OnSelectedItemPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            MultiselectAutoCompleteBox source = d as MultiselectAutoCompleteBox;

            if (source._ignorePropertyChange)
            {
                source._ignorePropertyChange = false;
                return;
            }

            // Update the text display
            if (source._skipSelectedItemTextUpdate)
            {
                source._skipSelectedItemTextUpdate = false;
            }
            else
            {
                source.OnSelectedItemChanged(e.NewValue);
            }

            // Fire the SelectionChanged event
            List<object> removed = new List<object>();
            if (e.OldValue != null)
            {
                removed.Add(e.OldValue);
            }
            
            List<object> added = new List<object>();
            if (e.NewValue != null)
            {
                added.Add(e.NewValue);
            }

            source.OnSelectionChanged(new SelectionChangedEventArgs(
#if !SILVERLIGHT
                SelectionChangedEvent,
#endif
                removed,
                added));
        }

        public class SelectedItemChangedEventArgs : EventArgs
        {
            public object SelectedItem
            { 
                get; set;
            }

            public int EntryStartingPosition
            { 
                get; set;
            }

            public string SearchText
            { 
                get; set;
            }

            public int CurrentCursorPosition
            { 
                get; set;
            }

            public string TextBoxText
            { 
                get; set;
            }

            public SelectedItemChangedEventArgs(object selectedItem, int entryStartedPosition, string searchText, int currentCursorPosition, string textboxText)
            {
                SelectedItem = selectedItem;
                EntryStartingPosition = entryStartedPosition;
                SearchText = searchText;
                CurrentCursorPosition = currentCursorPosition;
                TextBoxText = textboxText;
            }

            public bool IsHandled
            { 
                get; set;
            }

            public string NewText
            { 
                get; set;
            }

            public int NewCursorPosition
            { 
                get; set;
            }
        }

        public event EventHandler<SelectedItemChangedEventArgs> SelectedItemChanging;

        private bool RaiseSelectedItemChanging(SelectedItemChangedEventArgs args)
        {
            if (SelectedItemChanging != null)
            {
                SelectedItemChanging.Invoke(this, args);
                return args.IsHandled;
            }

            return false;
        }

        public int? moveCursorTo = null;

        /// <summary>
        /// Called when the selected item is changed, updates the text value
        /// that is displayed in the text box part.
        /// </summary>
        /// <param name="newItem">The new item.</param>
        private void OnSelectedItemChanged(object newItem)
        {
            string text;
            int newCursorPosition = -1;
            moveCursorTo = null;
            SelectedItemChangedEventArgs args = new SelectedItemChangedEventArgs(newItem, EntryStartingPosition,
                                                                                 SearchText, CurrentCursorPosition,
                                                                                 TextBox.Text);

            if (RaiseSelectedItemChanging(args))
            {
                newCursorPosition = args.NewCursorPosition;
                text = args.NewText;
                IsEntryStarted = false;
            }
            else
            {
                if (newItem == null)
                {
                    text = SearchText;
                    this.SelectedItem = null;
                }
                else
                {
                    text = FormatValue(newItem, true);
                    this.SelectedItem = newItem;
                }

                if (IsMultiSelect)
                {
                    string newTextValue = TextBox.Text.Substring(0, Math.Min(EntryStartingPosition, TextBox.Text.Length)) + text;
                    if (CurrentCursorPosition < TextBox.Text.Length)
                    {
                        newTextValue += TextBox.Text.Substring(CurrentCursorPosition,
                                                               TextBox.Text.Length - CurrentCursorPosition);
                    }
                    newCursorPosition = EntryStartingPosition + text.Length;

                    text = newTextValue;
                    IsEntryStarted = false;
                }
            }

            // Update the Text property and the TextBox values
            UpdateTextValue(text);

            if (newCursorPosition >= 0 && TextBox.CaretIndex == 0)
            {
                TextBox.CaretIndex = newCursorPosition;
                moveCursorTo = newCursorPosition;

/*                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, 
                    new Action(() => TextBox.SelectionStart = newCursorPosition)
                    );*/
            }
                

            // Move the caret to the end of the text box
/*            if (TextBox != null && Text != null)
            {
                TextBox.SelectionStart = Text.Length;
            } */
        }

        #endregion public object SelectedItem

        #region public string Text
        /// <summary>
        /// Gets or sets the text in the text box portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> control.
        /// </summary>
        /// <value>The text in the text box portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> control.</value>
        public string Text
        {
            get { return GetValue(TextProperty) as string; }
            set { SetValue(TextProperty, value); }
        }

        /// <summary>
        /// Identifies the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.Text" />
        /// dependency property.
        /// </summary>
        /// <value>The identifier for the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.Text" />
        /// dependency property.</value>
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                "Text",
                typeof(string),
                typeof(MultiselectAutoCompleteBox),
                new PropertyMetadata(string.Empty, OnTextPropertyChanged));

        /// <summary>
        /// TextProperty property changed handler.
        /// </summary>
        /// <param name="d">MultiselectAutoCompleteBox that changed its Text.</param>
        /// <param name="e">Event arguments.</param>
        private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            MultiselectAutoCompleteBox source = d as MultiselectAutoCompleteBox;
            source.TextUpdated((string)e.NewValue, false);
        }

        #endregion public string Text

        #region public string SearchText
        /// <summary>
        /// Gets the text that is used to filter items in the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemsSource" />
        /// item collection.
        /// </summary>
        /// <value>The text that is used to filter items in the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemsSource" />
        /// item collection.</value>
        /// <remarks>
        /// The SearchText value is typically the same as the 
        /// Text property, but is set after the TextChanged event occurs 
        /// and before the Populating event.
        /// </remarks>
        public string SearchText
        {
            get { return (string)GetValue(SearchTextProperty); }

            private set
            {
                try
                {
                    _allowWrite = true;
                    SetValue(SearchTextProperty, value);
                }
                finally
                {
                    _allowWrite = false;
                }
            }
        }

        /// <summary>
        /// Identifies the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.SearchText" />
        /// dependency property.
        /// </summary>
        /// <value>The identifier for the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.SearchText" />
        /// dependency property.</value>
        public static readonly DependencyProperty SearchTextProperty =
            DependencyProperty.Register(
                "SearchText",
                typeof(string),
                typeof(MultiselectAutoCompleteBox),
                new PropertyMetadata(string.Empty, OnSearchTextPropertyChanged));

        /// <summary>
        /// OnSearchTextProperty property changed handler.
        /// </summary>
        /// <param name="d">MultiselectAutoCompleteBox that changed its SearchText.</param>
        /// <param name="e">Event arguments.</param>
        private static void OnSearchTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            MultiselectAutoCompleteBox source = d as MultiselectAutoCompleteBox;
            if (source._ignorePropertyChange)
            {
                source._ignorePropertyChange = false;
                return;
            }

            // Ensure the property is only written when expected
            if (!source._allowWrite)
            {
                // Reset the old value before it was incorrectly written
                source._ignorePropertyChange = true;
                source.SetValue(e.Property, e.OldValue);

                throw new InvalidOperationException(Properties.Resources.AutoComplete_OnSearchTextPropertyChanged_InvalidWrite);
            }
        }
        #endregion public string SearchText

        #region public int MaxSuggestionsNumber
        public int MaxSuggestionsNumber
        {
            get { return (int ) GetValue(MaxSuggestionsNumberProperty); }

            private set
            {
                SetValue(MaxSuggestionsNumberProperty, value);
            }
        }

        /// <summary>
        /// Identifies the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.SearchText" />
        /// dependency property.
        /// </summary>
        /// <value>The identifier for the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.SearchText" />
        /// dependency property.</value>
        public static readonly DependencyProperty MaxSuggestionsNumberProperty =
            DependencyProperty.Register(
                "MaxSuggestionsNumber",
                typeof(int),
                typeof(MultiselectAutoCompleteBox)
                );
       
        #endregion public string SearchText

        #region public AutoCompleteFilterMode FilterMode
        /// <summary>
        /// Gets or sets how the text in the text box is used to filter items
        /// specified by the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemsSource" />
        /// property for display in the drop-down.
        /// </summary>
        /// <value>One of the
        /// <see cref="T:System.Windows.Controls.AutoCompleteFilterMode" />
        /// values The default is
        /// <see cref="F:System.Windows.Controls.AutoCompleteFilterMode.StartsWith" />.</value>
        /// <exception cref="T:System.ArgumentException">The specified value is
        /// not a valid
        /// <see cref="T:System.Windows.Controls.AutoCompleteFilterMode" />.</exception>
        /// <remarks>
        /// Use the FilterMode property to specify how possible matches are 
        /// filtered. For example, possible matches can be filtered in a 
        /// predefined or custom way. The search mode is automatically set to 
        /// Custom if you set the ItemFilter property. 
        /// </remarks>
        public AutoCompleteFilterMode FilterMode
        {
            get { return (AutoCompleteFilterMode)GetValue(FilterModeProperty); }
            set { SetValue(FilterModeProperty, value); }
        }

        /// <summary>
        /// Gets the identifier for the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.FilterMode" />
        /// dependency property.
        /// </summary>
        public static readonly DependencyProperty FilterModeProperty =
            DependencyProperty.Register(
                "FilterMode",
                typeof(AutoCompleteFilterMode),
                typeof(MultiselectAutoCompleteBox),
                new PropertyMetadata(AutoCompleteFilterMode.StartsWith, OnFilterModePropertyChanged));

        /// <summary>
        /// FilterModeProperty property changed handler.
        /// </summary>
        /// <param name="d">MultiselectAutoCompleteBox that changed its FilterMode.</param>
        /// <param name="e">Event arguments.</param>
        [SuppressMessage("Microsoft.Usage", "CA2208:InstantiateArgumentExceptionsCorrectly", Justification = "The exception will be thrown when the CLR setter is used in most situations.")]
        private static void OnFilterModePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            MultiselectAutoCompleteBox source = d as MultiselectAutoCompleteBox;
            AutoCompleteFilterMode mode = (AutoCompleteFilterMode)e.NewValue;

            if (mode != AutoCompleteFilterMode.Contains &&
                mode != AutoCompleteFilterMode.ContainsCaseSensitive &&
                mode != AutoCompleteFilterMode.ContainsOrdinal &&
                mode != AutoCompleteFilterMode.ContainsOrdinalCaseSensitive &&
                mode != AutoCompleteFilterMode.Custom && 
                mode != AutoCompleteFilterMode.Equals &&
                mode != AutoCompleteFilterMode.EqualsCaseSensitive &&
                mode != AutoCompleteFilterMode.EqualsOrdinal &&
                mode != AutoCompleteFilterMode.EqualsOrdinalCaseSensitive &&
                mode != AutoCompleteFilterMode.None &&
                mode != AutoCompleteFilterMode.StartsWith &&
                mode != AutoCompleteFilterMode.StartsWithCaseSensitive &&
                mode != AutoCompleteFilterMode.StartsWithOrdinal &&
                mode != AutoCompleteFilterMode.StartsWithOrdinalCaseSensitive)
            {
                source.SetValue(e.Property, e.OldValue);

                throw new ArgumentException(Properties.Resources.AutoComplete_OnFilterModePropertyChanged_InvalidValue, "value");
            }

            // Sets the filter predicate for the new value
            AutoCompleteFilterMode newValue = (AutoCompleteFilterMode)e.NewValue;
            source.TextFilter = AutoCompleteSearch.GetFilter(newValue);
        }
        #endregion public AutoCompleteFilterMode FilterMode

        #region public AutoCompleteFilterPredicate ItemFilter
        /// <summary>
        /// Gets or sets the custom method that uses user-entered text to filter
        /// the items specified by the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemsSource" />
        /// property for display in the drop-down.
        /// </summary>
        /// <value>The custom method that uses the user-entered text to filter
        /// the items specified by the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemsSource" />
        /// property. The default is null.</value>
        /// <remarks>
        /// The filter mode is automatically set to Custom if you set the 
        /// ItemFilter property. 
        /// </remarks>
        public AutoCompleteFilterPredicate<object> ItemFilter
        {
            get { return GetValue(ItemFilterProperty) as AutoCompleteFilterPredicate<object>; }
            set { SetValue(ItemFilterProperty, value); }
        }

        /// <summary>
        /// Identifies the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemFilter" />
        /// dependency property.
        /// </summary>
        /// <value>The identifier for the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemFilter" />
        /// dependency property.</value>
        public static readonly DependencyProperty ItemFilterProperty =
            DependencyProperty.Register(
                "ItemFilter",
                typeof(AutoCompleteFilterPredicate<object>),
                typeof(MultiselectAutoCompleteBox),
                new PropertyMetadata(OnItemFilterPropertyChanged));

        /// <summary>
        /// ItemFilterProperty property changed handler.
        /// </summary>
        /// <param name="d">MultiselectAutoCompleteBox that changed its ItemFilter.</param>
        /// <param name="e">Event arguments.</param>
        private static void OnItemFilterPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            MultiselectAutoCompleteBox source = d as MultiselectAutoCompleteBox;
            AutoCompleteFilterPredicate<object> value = e.NewValue as AutoCompleteFilterPredicate<object>;
            
            // If null, revert to the "None" predicate
            if (value == null)
            {
                source.FilterMode = AutoCompleteFilterMode.None;
            }
            else
            {
                source.FilterMode = AutoCompleteFilterMode.Custom;
                source.TextFilter = null;
            }
        }
        #endregion public AutoCompleteFilterPredicate ItemFilter

        #region public AutoCompleteStringFilterPredicate TextFilter
        /// <summary>
        /// Gets or sets the custom method that uses the user-entered text to
        /// filter items specified by the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemsSource" />
        /// property in a text-based way for display in the drop-down.
        /// </summary>
        /// <value>The custom method that uses the user-entered text to filter
        /// items specified by the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemsSource" />
        /// property in a text-based way for display in the drop-down.</value>
        /// <remarks>
        /// The search mode is automatically set to Custom if you set the 
        /// TextFilter property. 
        /// </remarks>
        public AutoCompleteFilterPredicate<string> TextFilter
        {
            get { return GetValue(TextFilterProperty) as AutoCompleteFilterPredicate<string>; }
            set { SetValue(TextFilterProperty, value); }
        }

        /// <summary>
        /// Identifies the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.TextFilter" />
        /// dependency property.
        /// </summary>
        /// <value>The identifier for the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.TextFilter" />
        /// dependency property.</value>
        public static readonly DependencyProperty TextFilterProperty =
            DependencyProperty.Register(
                "TextFilter",
                typeof(AutoCompleteFilterPredicate<string>),
                typeof(MultiselectAutoCompleteBox),
                new PropertyMetadata(AutoCompleteSearch.GetFilter(AutoCompleteFilterMode.StartsWith)));
        #endregion public AutoCompleteStringFilterPredicate TextFilter

        #region Template parts

        /// <summary>
        /// Gets or sets the drop down popup control.
        /// </summary>
        private PopupHelper DropDownPopup { get; set; }

        /// <summary>
        /// The TextBox template part.
        /// </summary>
        private TextBox _text;

        /// <summary>
        /// The SelectionAdapter.
        /// </summary>
        private ISelectionAdapter _adapter;

        /// <summary>
        /// Gets or sets the Text template part.
        /// </summary>
        internal TextBox TextBox
        {
            get { return _text; }
            set
            {
                // Detach existing handlers
                if (_text != null)
                {
                    _text.SelectionChanged -= OnTextBoxSelectionChanged;
                    _text.TextChanged -= OnTextBoxTextChanged;
                }

                _text = value;

                // Attach handlers
                if (_text != null)
                {
                    _text.SelectionChanged += OnTextBoxSelectionChanged;
                    _text.TextChanged += OnTextBoxTextChanged;
                    _text.PreviewLostKeyboardFocus += new KeyboardFocusChangedEventHandler(OnTextBoxPreviewLostKeyboardFocus);
                    _text.PreviewGotKeyboardFocus += new KeyboardFocusChangedEventHandler(OnTextBoxPreviewGotKeyboardFocus);

                    if (Text != null)
                    {
                        UpdateTextValue(Text);
                    }
                }
            }
        }

        private void OnTextBoxPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
//            Debug.WriteLine("Get Focus(), CaretIndex = " + TextBox.CaretIndex);
            hasFocus = true;
        }

        private bool hasFocus;
        void OnTextBoxPreviewLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
//           Debug.WriteLine("Lost Focus(), CaretIndex = " + TextBox.CaretIndex);
            hasFocus = false;
            currentCursorPosition = TextBox.CaretIndex;
        }

        /// <summary>
        /// Gets or sets the selection adapter used to populate the drop-down
        /// with a list of selectable items.
        /// </summary>
        /// <value>The selection adapter used to populate the drop-down with a
        /// list of selectable items.</value>
        /// <remarks>
        /// You can use this property when you create an automation peer to 
        /// use with MultiselectAutoCompleteBox or deriving from MultiselectAutoCompleteBox to 
        /// create a custom control.
        /// </remarks>
        protected internal ISelectionAdapter SelectionAdapter 
        { 
            get { return _adapter; }
            set
            {
                if (_adapter != null)
                {
                    _adapter.SelectionChanged -= OnAdapterSelectionChanged;
                    _adapter.Commit -= OnAdapterSelectionComplete;
                    _adapter.Commit -= OnAdapterCommit;
                    _adapter.Cancel -= OnAdapterSelectionCanceled;
                    _adapter.Cancel -= OnAdapterSelectionComplete;
                    _adapter.ItemsSource = null;
                }

                _adapter = value;

                if (_adapter != null)
                {
                    _adapter.SelectionChanged += OnAdapterSelectionChanged; 
                    _adapter.Commit += OnAdapterSelectionComplete;
                    _adapter.Commit += OnAdapterCommit;
                    _adapter.Cancel += OnAdapterSelectionCanceled;
                    _adapter.Cancel += OnAdapterSelectionComplete;
                    _adapter.ItemsSource = _view;
                }
            }
        }

        public event EventHandler SuggestionSelected;

        private void OnAdapterCommit(object sender, RoutedEventArgs e)
        {
            if (SuggestionSelected != null)
            {
                SuggestionSelected(this, EventArgs.Empty);
            }
        }

        #endregion

        /// <summary>
        /// Occurs when the text in the text box portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> changes.
        /// </summary>
#if SILVERLIGHT 
        public event RoutedEventHandler TextChanged;
#else
        public static readonly RoutedEvent TextChangedEvent = EventManager.RegisterRoutedEvent("TextChanged", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(MultiselectAutoCompleteBox));

        /// <summary>
        /// Occurs when the text in the text box portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> changes.
        /// </summary>
        public event RoutedEventHandler TextChanged
        {
            add { AddHandler(TextChangedEvent, value); }
            remove { RemoveHandler(TextChangedEvent, value); }
        }
#endif

        /// <summary>
        /// Occurs when the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> is
        /// populating the drop-down with possible matches based on the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.Text" />
        /// property.
        /// </summary>
        /// <remarks>
        /// If the event is canceled, by setting the PopulatingEventArgs.Cancel 
        /// property to true, the MultiselectAutoCompleteBox will not automatically 
        /// populate the selection adapter contained in the drop-down. 
        /// In this case, if you want possible matches to appear, you must 
        /// provide the logic for populating the selection adapter.
        /// </remarks>
#if SILVERLIGHT
        public event PopulatingEventHandler Populating;
#else
        public static readonly RoutedEvent PopulatingEvent = EventManager.RegisterRoutedEvent("Populating", RoutingStrategy.Bubble, typeof(PopulatingEventHandler), typeof(MultiselectAutoCompleteBox));

        /// <summary>
        /// Occurs when the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> is
        /// populating the drop-down with possible matches based on the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.Text" />
        /// property.
        /// </summary>
        /// <remarks>
        /// If the event is canceled, by setting the PopulatingEventArgs.Cancel 
        /// property to true, the MultiselectAutoCompleteBox will not automatically 
        /// populate the selection adapter contained in the drop-down. 
        /// In this case, if you want possible matches to appear, you must 
        /// provide the logic for populating the selection adapter.
        /// </remarks>
        public event PopulatingEventHandler Populating
        {
            add { AddHandler(PopulatingEvent, value); }
            remove { RemoveHandler(PopulatingEvent, value); }
        }
#endif

#if SILVERLIGHT
        public event PopulatedEventHandler Populated;
#else
        public static readonly RoutedEvent PopulatedEvent = EventManager.RegisterRoutedEvent("Populated", RoutingStrategy.Bubble, typeof(PopulatedEventHandler), typeof(MultiselectAutoCompleteBox));

        /// <summary>
        /// Occurs when the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> has
        /// populated the drop-down with possible matches based on the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.Text" />
        /// property.
        /// </summary>
        public event PopulatedEventHandler Populated
        {
            add { AddHandler(PopulatedEvent, value); }
            remove { RemoveHandler(PopulatedEvent, value); }
        }
#endif

        public static readonly RoutedEvent DropDownOpeningEvent = EventManager.RegisterRoutedEvent("DropDownOpening", RoutingStrategy.Bubble, typeof(RoutedPropertyChangingEventHandler<bool>), typeof(MultiselectAutoCompleteBox));

        /// <summary>
        /// Occurs when the value of the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.IsDropDownOpen" />
        /// property is changing from false to true.
        /// </summary>
        public event RoutedPropertyChangingEventHandler<bool> DropDownOpening
        {
            add { AddHandler(PopulatedEvent, value); }
            remove { RemoveHandler(PopulatedEvent, value); }
        }
        public static readonly RoutedEvent DropDownOpenedEvent = EventManager.RegisterRoutedEvent("DropDownOpened", RoutingStrategy.Bubble, typeof(RoutedPropertyChangedEventHandler<bool>), typeof(MultiselectAutoCompleteBox));

        /// <summary>
        /// Occurs when the value of the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.IsDropDownOpen" />
        /// property has changed from false to true and the drop-down is open.
        /// </summary>
        public event RoutedPropertyChangedEventHandler<bool> DropDownOpened
        {
            add { AddHandler(DropDownOpenedEvent, value); }
            remove { RemoveHandler(DropDownOpenedEvent, value); }
        }

        public static readonly RoutedEvent DropDownClosingEvent = EventManager.RegisterRoutedEvent("DropDownClosing", RoutingStrategy.Bubble, typeof(RoutedPropertyChangingEventHandler<bool>), typeof(MultiselectAutoCompleteBox));

        /// <summary>
        /// Occurs when the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.IsDropDownOpen" />
        /// property is changing from true to false.
        /// </summary>
        public event RoutedPropertyChangingEventHandler<bool> DropDownClosing
        {
            add { AddHandler(DropDownClosingEvent, value); }
            remove { RemoveHandler(DropDownClosingEvent, value); }
        }

        /// <summary>
        /// Occurs when the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.IsDropDownOpen" />
        /// property was changed from true to false and the drop-down is open.
        /// </summary>
#if SILVERLIGHT
        public event RoutedPropertyChangedEventHandler<bool> DropDownClosed;
#else
        public static readonly RoutedEvent DropDownClosedEvent = EventManager.RegisterRoutedEvent("DropDownClosed", RoutingStrategy.Bubble, typeof(RoutedPropertyChangedEventHandler<bool>), typeof(MultiselectAutoCompleteBox));

        /// <summary>
        /// Occurs when the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.IsDropDownOpen" />
        /// property was changed from true to false and the drop-down is open.
        /// </summary>
        public event RoutedPropertyChangedEventHandler<bool> DropDownClosed
        {
            add { AddHandler(DropDownClosedEvent, value); }
            remove { RemoveHandler(DropDownClosedEvent, value); }
        }
#endif

        /// <summary>
        /// Occurs when the selected item in the drop-down portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> has
        /// changed.
        /// </summary>
#if SILVERLIGHT
        public event SelectionChangedEventHandler SelectionChanged;
#else
        public static readonly RoutedEvent SelectionChangedEvent = EventManager.RegisterRoutedEvent("SelectionChanged", RoutingStrategy.Bubble, typeof(SelectionChangedEventHandler), typeof(MultiselectAutoCompleteBox));

        /// <summary>
        /// Occurs when the selected item in the drop-down portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> has
        /// changed.
        /// </summary>
        public event SelectionChangedEventHandler SelectionChanged
        {
            add { AddHandler(SelectionChangedEvent, value); }
            remove { RemoveHandler(SelectionChangedEvent, value); }
        }
#endif

        /// <summary>
        /// Gets or sets the  <see cref="T:System.Windows.Data.Binding" /> that
        /// is used to get the values for display in the text portion of
        /// the <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" />
        /// control.
        /// </summary>
        /// <value>The <see cref="T:System.Windows.Data.Binding" /> object used
        /// when binding to a collection property.</value>
        public Binding ValueMemberBinding
        {
            get
            {
                return _valueBindingEvaluator != null ? _valueBindingEvaluator.ValueBinding : null;
            }
            set
            {
                _valueBindingEvaluator = new BindingEvaluator<string>(value);
            }
        }

        /// <summary>
        /// Gets or sets the property path that is used to get values for
        /// display in the text portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> control.
        /// </summary>
        /// <value>The property path that is used to get values for display in
        /// the text portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> control.</value>
        public string ValueMemberPath
        {
            get
            {
                return (ValueMemberBinding != null) ? ValueMemberBinding.Path.Path : null;
            }
            set
            {
                ValueMemberBinding = value == null ? null : new Binding(value);
            }
        }

#if !SILVERLIGHT
        /// <summary>
        /// Initializes the static members of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> class.
        /// </summary>
        static MultiselectAutoCompleteBox()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(MultiselectAutoCompleteBox), new FrameworkPropertyMetadata(typeof(MultiselectAutoCompleteBox)));
        }
#endif

        /// <summary>
        /// Initializes a new instance of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> class.
        /// </summary>
        public MultiselectAutoCompleteBox()
        {
#if SILVERLIGHT  
            DefaultStyleKey = typeof(MultiselectAutoCompleteBox);

            Loaded += (sender, e) => ApplyTemplate();
#endif
            MaxSuggestionsNumber = 500;
            IsEnabledChanged += ControlIsEnabledChanged;

            Interaction = new InteractionHelper(this);

            // Creating the view here ensures that View is always != null
            ClearView();
        }

        /// <summary>
        /// Arranges and sizes the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" />
        /// control and its contents.
        /// </summary>
        /// <param name="finalSize">The size allowed for the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> control.</param>
        /// <returns>The <paramref name="finalSize" />, unchanged.</returns>
        protected override Size ArrangeOverride(Size finalSize)
        {
            Size r = base.ArrangeOverride(finalSize);
            if (DropDownPopup != null)
            {
                DropDownPopup.Arrange();
            }
            return r;
        }

        private ListBox itemSelectorControl;

        /// <summary>
        /// Builds the visual tree for the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> control
        /// when a new template is applied.
        /// </summary>
        public override void OnApplyTemplate()
        {
#if !SILVERLIGHT
            if (TextBox != null)
            {
                TextBox.PreviewKeyDown -= OnTextBoxPreviewKeyDown;
                TextBox.PreviewMouseDown -= OnTexBoxMouseDown;
            }
#endif
            if (DropDownPopup != null)
            {
                DropDownPopup.Closed -= DropDownPopup_Closed;
                DropDownPopup.FocusChanged -= OnDropDownFocusChanged;
                DropDownPopup.UpdateVisualStates -= OnDropDownPopupUpdateVisualStates;
                DropDownPopup.BeforeOnApplyTemplate();
                DropDownPopup = null;
            }

            base.OnApplyTemplate();

            // Set the template parts. Individual part setters remove and add 
            // any event handlers.
            Popup popup = GetTemplateChild(ElementPopup) as Popup;
            if (popup != null)
            {
                DropDownPopup = new PopupHelper(this, popup);
                DropDownPopup.MaxDropDownHeight = MaxDropDownHeight;
                DropDownPopup.AfterOnApplyTemplate();
                DropDownPopup.Closed += DropDownPopup_Closed;
                DropDownPopup.FocusChanged += OnDropDownFocusChanged;
                DropDownPopup.UpdateVisualStates += OnDropDownPopupUpdateVisualStates;
            }
            SelectionAdapter = GetSelectionAdapterPart();
            TextBox = GetTemplateChild(MultiselectAutoCompleteBox.ElementTextBox) as TextBox;
#if !SILVERLIGHT
            if (TextBox != null)
            {
                TextBox.PreviewKeyDown += OnTextBoxPreviewKeyDown;
                TextBox.PreviewMouseDown += OnTexBoxMouseDown;
            }
#endif
            Interaction.OnApplyTemplateBase();

            // If the drop down property indicates that the popup is open,
            // flip its value to invoke the changed handler.
            if (IsDropDownOpen && DropDownPopup != null && !DropDownPopup.IsOpen)
            {
                OpeningDropDown(false);
            }

            itemSelectorControl = (ListBox) GetTemplateChild("Selector");
        }

        private void OnTexBoxMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsDropDownOpen)
            {
                IsDropDownOpen = false;
                moveCursorTo = null; // don't change cursor position in this case
            }
        }

        /// <summary>
        /// Allows the popup wrapper to fire visual state change events.
        /// </summary>
        /// <param name="sender">The source object.</param>
        /// <param name="e">The event data.</param>
        private void OnDropDownPopupUpdateVisualStates(object sender, EventArgs e)
        {
            UpdateVisualState(true);
        }

        /// <summary>
        /// Allows the popup wrapper to fire the FocusChanged event.
        /// </summary>
        /// <param name="sender">The source object.</param>
        /// <param name="e">The event data.</param>
        private void OnDropDownFocusChanged(object sender, EventArgs e)
        {
            FocusChanged(HasFocus());
        }

        /// <summary>
        /// Begin closing the drop-down.
        /// </summary>
        /// <param name="oldValue">The original value.</param>
        private void ClosingDropDown(bool oldValue)
        {
            bool delayedClosingVisual = false;
            if (DropDownPopup != null)
            {
                delayedClosingVisual = DropDownPopup.UsesClosingVisualState;
            }

#if SILVERLIGHT
            RoutedPropertyChangingEventArgs<bool> args = new RoutedPropertyChangingEventArgs<bool>(IsDropDownOpenProperty, oldValue, false, true);
#else
            RoutedPropertyChangingEventArgs<bool> args = new RoutedPropertyChangingEventArgs<bool>(IsDropDownOpenProperty, oldValue, false, true, DropDownClosingEvent);
#endif

            OnDropDownClosing(args);

            if (_view == null || _view.Count == 0)
            {
                delayedClosingVisual = false;
            }

            if (args.Cancel)
            {
                _ignorePropertyChange = true;
                SetValue(IsDropDownOpenProperty, oldValue);
            }
            else
            {
                // Immediately close the drop down window:
                // When a popup closed visual state is present, the code path is 
                // slightly different and the actual call to CloseDropDown will 
                // be called only after the visual state's transition is done
                RaiseExpandCollapseAutomationEvent(oldValue, false);
                if (!delayedClosingVisual)
                {
                    CloseDropDown(oldValue, false);
                }
            }

            UpdateVisualState(true);
        }

        /// <summary>
        /// Begin opening the drop down by firing cancelable events, opening the
        /// drop-down or reverting, depending on the event argument values.
        /// </summary>
        /// <param name="oldValue">The original value, if needed for a revert.</param>
        private void OpeningDropDown(bool oldValue)
        {
#if SILVERLIGHT
            RoutedPropertyChangingEventArgs<bool> args = new RoutedPropertyChangingEventArgs<bool>(IsDropDownOpenProperty, oldValue, true, true);
#else
            RoutedPropertyChangingEventArgs<bool> args = new RoutedPropertyChangingEventArgs<bool>(IsDropDownOpenProperty, oldValue, true, true, DropDownOpeningEvent);
#endif
            // Opening
            OnDropDownOpening(args);

            if (args.Cancel)
            {
                _ignorePropertyChange = true;
                SetValue(IsDropDownOpenProperty, oldValue);
            }
            else
            {
                RaiseExpandCollapseAutomationEvent(oldValue, true);
                OpenDropDown(oldValue, true);
            }

            UpdateVisualState(true);
        }

        /// <summary>
        /// Raise an expand/collapse event through the automation peer.
        /// </summary>
        /// <param name="oldValue">The old value.</param>
        /// <param name="newValue">The new value.</param>
        private void RaiseExpandCollapseAutomationEvent(bool oldValue, bool newValue)
        {
            AutoCompleteBoxAutomationPeer peer = FrameworkElementAutomationPeer.FromElement(this) as AutoCompleteBoxAutomationPeer;
            if (peer != null)
            {
                peer.RaiseExpandCollapseAutomationEvent(oldValue, newValue);
            }
        }

#if !SILVERLIGHT
        /// <summary>
        /// Handles the PreviewKeyDown event on the TextBox for WPF. This method
        /// is not implemented for Silverlight.
        /// </summary>
        /// <param name="sender">The source object.</param>
        /// <param name="e">The event data.</param>
        private void OnTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            OnKeyDown(e);
        }
#endif

        /// <summary>
        /// Connects to the DropDownPopup Closed event.
        /// </summary>
        /// <param name="sender">The source object.</param>
        /// <param name="e">The event data.</param>
        private void DropDownPopup_Closed(object sender, EventArgs e)
        {
            // Force the drop down dependency property to be false.
            if (IsDropDownOpen)
            {
                IsDropDownOpen = false;
            }

            if (moveCursorTo != null)
            {
                if (moveCursorTo != TextBox.CaretIndex - 1)
                    TextBox.CaretIndex = (int) moveCursorTo;
                moveCursorTo = null;
            }

            // Fire the DropDownClosed event
            if (_popupHasOpened)
            {
#if SILVERLIGHT
                OnDropDownClosed(new RoutedPropertyChangedEventArgs<bool>(true, false));
#else
                OnDropDownClosed(new RoutedPropertyChangedEventArgs<bool>(true, false, DropDownClosedEvent));
#endif
            }
        }

        /// <summary>
        /// Creates an
        /// <see cref="T:System.Windows.Automation.Peers.AutoCompleteBoxAutomationPeer" />
        /// </summary>
        /// <returns>A
        /// <see cref="T:System.Windows.Automation.Peers.AutoCompleteBoxAutomationPeer" />
        /// for the <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" />
        /// object.</returns>
        protected override AutomationPeer OnCreateAutomationPeer()
        {
            return new MultiselectAutoCompleteBoxAutomationPeer(this);
        }

        #region Focus

        /// <summary>
        /// Handles the FocusChanged event.
        /// </summary>
        /// <param name="hasFocus">A value indicating whether the control 
        /// currently has the focus.</param>
        private void FocusChanged(bool hasFocus)
        {
            // The OnGotFocus & OnLostFocus are asynchronously and cannot 
            // reliably tell you that have the focus.  All they do is let you 
            // know that the focus changed sometime in the past.  To determine 
            // if you currently have the focus you need to do consult the 
            // FocusManager (see HasFocus()).

            if (hasFocus)
            {
/*                if (TextBox != null && TextBox.SelectionLength == 0)
                {
                    TextBox.SelectAll();
                }
 * 
 * */
            }
            else
            {
                IsDropDownOpen = false;
                _userCalledPopulate = false;
/*                if (TextBox != null)
                {
                    TextBox.Select(TextBox.Text.Length, 0);
                }
 * */
            }
        }

        /// <summary>
        /// Determines whether the text box or drop-down portion of the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> control has
        /// focus.
        /// </summary>
        /// <returns>true to indicate the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> has focus;
        /// otherwise, false.</returns>
        protected bool HasFocus()
        {
            DependencyObject focused =
#if SILVERLIGHT
                FocusManager.GetFocusedElement() as DependencyObject;
#else
                // For WPF, check if the element that has focus is within the control, as
                // FocusManager.GetFocusedElement(this) will return null in such a case.
                this.IsKeyboardFocusWithin ? Keyboard.FocusedElement as DependencyObject : FocusManager.GetFocusedElement(this) as DependencyObject;
#endif
            while (focused != null)
            {
                if (object.ReferenceEquals(focused, this))
                {
                    return true;
                }

                // This helps deal with popups that may not be in the same 
                // visual tree
                DependencyObject parent = VisualTreeHelper.GetParent(focused);
                if (parent == null)
                {
                    // Try the logical parent.
                    FrameworkElement element = focused as FrameworkElement;
                    if (element != null)
                    {
                        parent = element.Parent;
                    }
                }
                focused = parent;
            }
            return false;
        }

        /// <summary>
        /// Provides handling for the
        /// <see cref="E:System.Windows.UIElement.GotFocus" /> event.
        /// </summary>
        /// <param name="e">A <see cref="T:System.Windows.RoutedEventArgs" />
        /// that contains the event data.</param>
        protected override void OnGotFocus(RoutedEventArgs e)
        {
            base.OnGotFocus(e);
            FocusChanged(HasFocus());
        }

#if !SILVERLIGHT
        /// <summary>
        /// Handles change of keyboard focus, which is treated differently than control focus
        /// </summary>
        /// <param name="e"></param>
        protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnIsKeyboardFocusWithinChanged(e);
            FocusChanged((bool)e.NewValue);
        }
#endif

        /// <summary>
        /// Provides handling for the
        /// <see cref="E:System.Windows.UIElement.LostFocus" /> event.
        /// </summary>
        /// <param name="e">A <see cref="T:System.Windows.RoutedEventArgs" />
        /// that contains the event data.</param>
        protected override void OnLostFocus(RoutedEventArgs e)
        {
            base.OnLostFocus(e);
            FocusChanged(HasFocus());
        }

        #endregion

        /// <summary>
        /// Handle the change of the IsEnabled property.
        /// </summary>
        /// <param name="sender">The source object.</param>
        /// <param name="e">The event data.</param>
        private void ControlIsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            bool isEnabled = (bool)e.NewValue;
            if (!isEnabled)
            {
                IsDropDownOpen = false;
            }
        }

        /// <summary>
        /// Returns the
        /// <see cref="T:System.Windows.Controls.ISelectionAdapter" /> part, if
        /// possible.
        /// </summary>
        /// <returns>
        /// A <see cref="T:System.Windows.Controls.ISelectionAdapter" /> object,
        /// if possible. Otherwise, null.
        /// </returns>
        [SuppressMessage("Microsoft.Design", "CA1024:UsePropertiesWhereAppropriate", Justification = "Following the GetTemplateChild pattern for the method.")]
        protected virtual ISelectionAdapter GetSelectionAdapterPart()
        {
            ISelectionAdapter adapter = null;
            Selector selector = GetTemplateChild(ElementSelector) as Selector;
            if (selector != null)
            {
                // Check if it is already an IItemsSelector
                adapter = selector as ISelectionAdapter;
                if (adapter == null)
                {
                    // Built in support for wrapping a Selector control
                    adapter = new SelectorSelectionAdapter(selector);
                }
            }
            if (adapter == null)
            {
                adapter = GetTemplateChild(ElementSelectionAdapter) as ISelectionAdapter;
            }
            return adapter;
        }

        /// <summary>
        /// Handles the timer tick when using a populate delay.
        /// </summary>
        /// <param name="sender">The source object.</param>
        /// <param name="e">The event arguments.</param>
        private void PopulateDropDown(object sender, EventArgs e)
        {
            if (_delayTimer != null)
            {
                _delayTimer.Stop();
            }

            // Update the prefix/search text.
            string searchText = Text ?? String.Empty;
            if (IsMultiSelect && searchText.Length > 0)
            {
                if (CurrentCursorPosition > EntryStartingPosition)
                    searchText = searchText.Substring(EntryStartingPosition, CurrentCursorPosition - EntryStartingPosition);
                else
                    searchText = String.Empty;
            }
            SearchText = searchText;

            PopulatingEventArgs populating = new PopulatingEventArgs(SearchText, PopulatingEvent);

            OnPopulating(populating);
            if (!populating.Cancel)
            {
                PopulateComplete();
            }
        }

        /// <summary>
        /// Raises the
        /// <see cref="E:System.Windows.Controls.MultiselectAutoCompleteBox.Populating" />
        /// event.
        /// </summary>
        /// <param name="e">A
        /// <see cref="T:System.Windows.Controls.PopulatingEventArgs" /> that
        /// contains the event data.</param>
        protected virtual void OnPopulating(PopulatingEventArgs e)
        {
            RaiseEvent(e);
        }

        /// <summary>
        /// Raises the
        /// <see cref="E:System.Windows.Controls.MultiselectAutoCompleteBox.Populated" />
        /// event.
        /// </summary>
        /// <param name="e">A
        /// <see cref="T:System.Windows.Controls.PopulatedEventArgs" />
        /// that contains the event data.</param>
        protected virtual void OnPopulated(PopulatedEventArgs e)
        {
            RaiseEvent(e);
        }

        /// <summary>
        /// Raises the
        /// <see cref="E:System.Windows.Controls.MultiselectAutoCompleteBox.SelectionChanged" />
        /// event.
        /// </summary>
        /// <param name="e">A
        /// <see cref="T:System.Windows.Controls.SelectionChangedEventArgs" />
        /// that contains the event data.</param>
        protected virtual void OnSelectionChanged(SelectionChangedEventArgs e)
        {
#if SILVERLIGHT
            SelectionChangedEventHandler handler = SelectionChanged;
            if (handler != null)
            {
                handler(this, e);
            }
#else
            RaiseEvent(e);
#endif
        }

        /// <summary>
        /// Raises the
        /// <see cref="E:System.Windows.Controls.MultiselectAutoCompleteBox.DropDownOpening" />
        /// event.
        /// </summary>
        /// <param name="e">A
        /// <see cref="T:System.Windows.Controls.RoutedPropertyChangingEventArgs`1" />
        /// that contains the event data.</param>
        protected virtual void OnDropDownOpening(RoutedPropertyChangingEventArgs<bool> e)
        {
#if SILVERLIGHT
            RoutedPropertyChangingEventHandler<bool> handler = DropDownOpening;
            if (handler != null)
            {
                handler(this, e);
            }
#else
            RaiseEvent(e);
#endif
        }

        /// <summary>
        /// Raises the
        /// <see cref="E:System.Windows.Controls.MultiselectAutoCompleteBox.DropDownOpened" />
        /// event.
        /// </summary>
        /// <param name="e">A
        /// <see cref="T:System.Windows.RoutedPropertyChangedEventArgs`1" />
        /// that contains the event data.</param>
        protected virtual void OnDropDownOpened(RoutedPropertyChangedEventArgs<bool> e)
        {
#if SILVERLIGHT
            RoutedPropertyChangedEventHandler<bool> handler = DropDownOpened;
            if (handler != null)
            {
                handler(this, e);
            }
#else
            RaiseEvent(e);
#endif
        }

        /// <summary>
        /// Raises the
        /// <see cref="E:System.Windows.Controls.MultiselectAutoCompleteBox.DropDownClosing" />
        /// event.
        /// </summary>
        /// <param name="e">A
        /// <see cref="T:System.Windows.Controls.RoutedPropertyChangingEventArgs`1" />
        /// that contains the event data.</param>
        protected virtual void OnDropDownClosing(RoutedPropertyChangingEventArgs<bool> e)
        {
#if SILVERLIGHT
            RoutedPropertyChangingEventHandler<bool> handler = DropDownClosing;
            if (handler != null)
            {
                handler(this, e);
            }
#else
            RaiseEvent(e);
#endif
        }

        /// <summary>
        /// Raises the
        /// <see cref="E:System.Windows.Controls.MultiselectAutoCompleteBox.DropDownClosed" />
        /// event.
        /// </summary>
        /// <param name="e">A
        /// <see cref="T:System.Windows.RoutedPropertyChangedEventArgs`1" />
        /// which contains the event data.</param>
        protected virtual void OnDropDownClosed(RoutedPropertyChangedEventArgs<bool> e)
        {
#if SILVERLIGHT
            RoutedPropertyChangedEventHandler<bool> handler = DropDownClosed;
            if (handler != null)
            {
                handler(this, e);
            }
#else
            RaiseEvent(e);
#endif
        }

        /// <summary>
        /// Formats an Item for text comparisons based on Converter 
        /// and ConverterCulture properties.
        /// </summary>
        /// <param name="value">The object to format.</param>
        /// <param name="clearDataContext">A value indicating whether to clear
        /// the data context after the lookup is performed.</param>
        /// <returns>Formatted Value.</returns>
        private string FormatValue(object value, bool clearDataContext)
        {
            string str = FormatValue(value);
            if (clearDataContext && _valueBindingEvaluator != null)
            {
                _valueBindingEvaluator.ClearDataContext();
            }
            return str;
        }

        /// <summary>
        /// Converts the specified object to a string by using the
        /// <see cref="P:System.Windows.Data.Binding.Converter" /> and
        /// <see cref="P:System.Windows.Data.Binding.ConverterCulture" /> values
        /// of the binding object specified by the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ValueMemberBinding" />
        /// property.
        /// </summary>
        /// <param name="value">The object to format as a string.</param>
        /// <returns>The string representation of the specified object.</returns>
        /// <remarks>
        /// Override this method to provide a custom string conversion.
        /// </remarks>
        protected virtual string FormatValue(object value)
        {
            if (_valueBindingEvaluator != null)
            {
                return _valueBindingEvaluator.GetDynamicValue(value) ?? string.Empty;
            }

            return value == null ? string.Empty : value.ToString();
        }

        /// <summary>
        /// Raises the
        /// <see cref="E:System.Windows.Controls.MultiselectAutoCompleteBox.TextChanged" />
        /// event.
        /// </summary>
        /// <param name="e">A <see cref="T:System.Windows.RoutedEventArgs" />
        /// that contains the event data.</param>
        protected virtual void OnTextChanged(RoutedEventArgs e)
        {
#if SILVERLIGHT
            RoutedEventHandler handler = TextChanged;
            if (handler != null)
            {
                handler(this, e);
            }
#else
            RaiseEvent(e);
#endif
        }

        /// <summary>
        /// Handle the TextChanged event that is directly attached to the 
        /// TextBox part. This ensures that only user initiated actions will 
        /// result in an MultiselectAutoCompleteBox suggestion and operation.
        /// </summary>
        /// <param name="sender">The source TextBox object.</param>
        /// <param name="e">The TextChanged event data.</param>
        private void OnTextBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            // Call the central updated text method as a user-initiated action
            TextUpdated(_text.Text, true);
        }


        private int currentCursorPosition = 0;
        public int CurrentCursorPosition
        {
            get
            {
                if (hasFocus)
                    return TextBox.SelectionStart;
                else
                {
                    return currentCursorPosition;
                }
            }
        }

        /// <summary>
        /// When selection changes, save the location of the selection start.
        /// </summary>
        /// <param name="sender">The source object.</param>
        /// <param name="e">The event data.</param>
        private void OnTextBoxSelectionChanged(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("OnTextBoxSelectionChanged: " + _text.SelectionStart + ", HasFocus " + hasFocus);

            // If ignoring updates. This happens after text is updated, and 
            // before the PopulateComplete method is called. Required for the 
            // IsTextCompletionEnabled feature.
            if (_ignoreTextSelectionChange)
            {
                return;
            }

            _textSelectionStart = _text.SelectionStart;
        }

        /// <summary>
        /// Updates both the text box value and underlying text dependency 
        /// property value if and when they change. Automatically fires the 
        /// text changed events when there is a change.
        /// </summary>
        /// <param name="value">The new string value.</param>
        private void UpdateTextValue(string value)
        {
            UpdateTextValue(value, null);
        }

        /// <summary>
        /// Updates both the text box value and underlying text dependency 
        /// property value if and when they change. Automatically fires the 
        /// text changed events when there is a change.
        /// </summary>
        /// <param name="value">The new string value.</param>
        /// <param name="userInitiated">A nullable bool value indicating whether
        /// the action was user initiated. In a user initiated mode, the 
        /// underlying text dependency property is updated. In a non-user 
        /// interaction, the text box value is updated. When user initiated is 
        /// null, all values are updated.</param>
        private void UpdateTextValue(string value, bool? userInitiated)
        {
            string newTextValue = value;

            // Update the Text dependency property
            if ((userInitiated == null || userInitiated == true) && Text != newTextValue)
            {
                _ignoreTextPropertyChange++;
                Text = newTextValue;
#if SILVERLIGHT
                OnTextChanged(new RoutedEventArgs());
#else
                OnTextChanged(new RoutedEventArgs(TextChangedEvent));
#endif
            }

            // Update the TextBox's Text dependency property
            if ((userInitiated == null || userInitiated == false) && TextBox != null && TextBox.Text != newTextValue)
            {
                _ignoreTextPropertyChange++;
                Debug.WriteLine("TextBox.Text is set, position: " + TextBox.CaretIndex);
                TextBox.Text = newTextValue ?? string.Empty;

                // Text dependency property value was set, fire event
                if (Text == newTextValue || Text == null)
                {
#if SILVERLIGHT
                    OnTextChanged(new RoutedEventArgs());
#else
                    OnTextChanged(new RoutedEventArgs(TextChangedEvent));
#endif
                }
            }
        }

        /// <summary>
        /// Handle the update of the text for the control from any source, 
        /// including the TextBox part and the Text dependency property.
        /// </summary>
        /// <param name="newText">The new text.</param>
        /// <param name="userInitiated">A value indicating whether the update 
        /// is a user-initiated action. This should be a True value when the 
        /// TextUpdated method is called from a TextBox event handler.</param>
        private void TextUpdated(string newText, bool userInitiated)
        {
            // Only process this event if it is coming from someone outside 
            // setting the Text dependency property directly.
            if (_ignoreTextPropertyChange > 0)
            {
                _ignoreTextPropertyChange--;
                return;
            }

            if (newText == null)
            {
                newText = string.Empty;
            }

            // The TextBox.TextChanged event was not firing immediately and 
            // was causing an immediate update, even with wrapping. If there is 
            // a selection currently, no update should happen.
            if (IsTextCompletionEnabled && TextBox != null && TextBox.SelectionLength > 0 && TextBox.SelectionStart != TextBox.Text.Length)
            {
                return;
            }

            // Evaluate the conditions needed for completion.
            bool populateReady = false;
            string currentSearchFilter = String.Empty;

            if (IsEntryStarted && CurrentCursorPosition < EntryStartingPosition)
            {
                IsEntryStarted = false;
            }

            Debug.WriteLine("IsEntryStarted: " + IsEntryStarted);
            if (IsMultiSelect && userInitiated && !IsEntryStarted)
            {
                IsEntryStarted = IsNewEntryStarts();
                EntryStartingPosition = CurrentCursorPosition > 0 ? TextBox.SelectionStart-1 : 0;
                currentSearchFilter = newText.Length > 0 ? newText.Substring(EntryStartingPosition, 1) : String.Empty;
            }
            
            if (IsEntryStarted)
            {
                if (String.IsNullOrEmpty(newText))
                {
                    currentSearchFilter = String.Empty;
                    EntryStartingPosition = 0;
                }
                else
                {
                    if (EntryStartingPosition > CurrentCursorPosition)
                    {
                        // search to the left
                        EntryStartingPosition = CurrentCursorPosition;
                    }
                    else
                    {
                        currentSearchFilter = newText.Substring(EntryStartingPosition,
                                                                CurrentCursorPosition - EntryStartingPosition);

                        for (int i = 0; i < currentSearchFilter.Length; i++)
                        {
                            if (!IsBlankChar(currentSearchFilter[i]))
                                break;

                            EntryStartingPosition++;
                        }
                    }

                    currentSearchFilter = newText.Substring(EntryStartingPosition,
                                                            CurrentCursorPosition - EntryStartingPosition);
                }
            }

            populateReady = IsEntryStarted && (MinimumPrefixLength <= 0 || currentSearchFilter.Length >= MinimumPrefixLength);
            _userCalledPopulate = populateReady ? userInitiated : false;

            // Update the interface and values only as necessary
            UpdateTextValue(newText, userInitiated);

            if (populateReady)
            {
                ShowDropDown();
            }
            else
            {
                SearchText = string.Empty;
                if (SelectedItem != null)
                {
                    _skipSelectedItemTextUpdate = true;
                }
                SelectedItem = null;
                if (IsDropDownOpen)
                {
                    IsDropDownOpen = false;
                }
            }
        }

        private void ShowDropDown()
        {
            _ignoreTextSelectionChange = true;

            if (_delayTimer != null)
            {
                _delayTimer.Start();
            }
            else
            {
                PopulateDropDown(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Notifies the
        /// <see cref="T:System.Windows.Controls.MultiselectAutoCompleteBox" /> that the
        /// <see cref="P:System.Windows.Controls.MultiselectAutoCompleteBox.ItemsSource" />
        /// property has been set and the data can be filtered to provide
        /// possible matches in the drop-down.
        /// </summary>
        /// <remarks>
        /// Call this method when you are providing custom population of 
        /// the drop-down portion of the MultiselectAutoCompleteBox, to signal the control 
        /// that you are done with the population process. 
        /// Typically, you use PopulateComplete when the population process 
        /// is a long-running process and you want to cancel built-in filtering
        ///  of the ItemsSource items. In this case, you can handle the 
        /// Populated event and set PopulatingEventArgs.Cancel to true. 
        /// When the long-running process has completed you call 
        /// PopulateComplete to indicate the drop-down is populated.
        /// </remarks>
        public void PopulateComplete()
        {
            // Apply the search filter
            RefreshView();

            // Fire the Populated event containing the read-only view data.
            PopulatedEventArgs populated = new PopulatedEventArgs(new ReadOnlyCollection<object>(_view), PopulatedEvent);
            OnPopulated(populated);

            if (SelectionAdapter != null && SelectionAdapter.ItemsSource != _view)
            {
                SelectionAdapter.ItemsSource = _view;
            }

            bool isDropDownOpen = /*_userCalledPopulate && */ (_view.Count > 0); // TODO: get rid of this userCalledPopulate
            if (isDropDownOpen != IsDropDownOpen)
            {
                _ignorePropertyChange = true;
                IsDropDownOpen = isDropDownOpen;
            }
            if (IsDropDownOpen)
            {
                OpeningDropDown(false);
                if (DropDownPopup != null)
                {
                    DropDownPopup.Arrange();
                }
            }
            else
            {
                ClosingDropDown(true);
            }

            UpdateTextCompletion(_userCalledPopulate);
        }

        /// <summary>
        /// Performs text completion, if enabled, and a lookup on the underlying
        /// item values for an exact match. Will update the SelectedItem value.
        /// </summary>
        /// <param name="userInitiated">A value indicating whether the operation
        /// was user initiated. Text completion will not be performed when not 
        /// directly initiated by the user.</param>
        private void UpdateTextCompletion(bool userInitiated)
        {
            return; // TODO: figure out whether we need it

            // By default this method will clear the selected value
            object newSelectedItem = null;
            string text = Text;

            // Text search is StartsWith explicit and only when enabled, in 
            // line with WPF's ComboBox lookup. When in use it will associate 
            // a Value with the Text if it is found in ItemsSource. This is 
            // only valid when there is data and the user initiated the action.
            if (_view.Count > 0)
            {
                if (IsTextCompletionEnabled && TextBox != null && userInitiated)
                {
                    int currentLength = TextBox.Text.Length;
                    int selectionStart = TextBox.SelectionStart;
                    if (selectionStart == text.Length && selectionStart > _textSelectionStart)
                    {
                        // When the FilterMode dependency property is set to 
                        // either StartsWith or StartsWithCaseSensitive, the 
                        // first item in the view is used. This will improve 
                        // performance on the lookup. It assumes that the 
                        // FilterMode the user has selected is an acceptable 
                        // case sensitive matching function for their scenario.
                        object top = FilterMode == AutoCompleteFilterMode.StartsWith || FilterMode == AutoCompleteFilterMode.StartsWithCaseSensitive
                            ? _view[0]
                            : TryGetMatch(text, _view, AutoCompleteSearch.GetFilter(AutoCompleteFilterMode.StartsWith));

                        // If the search was successful, update SelectedItem
                        if (top != null)
                        {
                            newSelectedItem = top;
                            string topString = FormatValue(top, true);

                            // Only replace partially when the two words being the same
                            int minLength = Math.Min(topString.Length, Text.Length);
                            if (AutoCompleteSearch.Equals(Text.Substring(0, minLength), topString.Substring(0, minLength)))
                            {
                                // Update the text
                                UpdateTextValue(topString);

                                // Select the text past the user's caret
                                TextBox.SelectionStart = currentLength;
                                TextBox.SelectionLength = topString.Length - currentLength;
                            }
                        }
                    }
                }
                else
                {
                    // Perform an exact string lookup for the text. This is a 
                    // design change from the original Toolkit release when the 
                    // IsTextCompletionEnabled property behaved just like the 
                    // WPF ComboBox's IsTextSearchEnabled property.
                    //
                    // This change provides the behavior that most people expect
                    // to find: a lookup for the value is always performed.
                    newSelectedItem = TryGetMatch(text, _view, AutoCompleteSearch.GetFilter(AutoCompleteFilterMode.EqualsCaseSensitive));
                }
            }

            // Update the selected item property

            if (SelectedItem != newSelectedItem)
            {
                _skipSelectedItemTextUpdate = true;
            }
            SelectedItem = newSelectedItem;

            // Restore updates for TextSelection
            if (_ignoreTextSelectionChange)
            {
                _ignoreTextSelectionChange = false;
                if (TextBox != null)
                {
                    _textSelectionStart = TextBox.SelectionStart;
                }
            }
        }

        /// <summary>
        /// Attempts to look through the view and locate the specific exact 
        /// text match.
        /// </summary>
        /// <param name="searchText">The search text.</param>
        /// <param name="view">The view reference.</param>
        /// <param name="predicate">The predicate to use for the partial or 
        /// exact match.</param>
        /// <returns>Returns the object or null.</returns>
        private object TryGetMatch(string searchText, ObservableCollection<object> view, AutoCompleteFilterPredicate<string> predicate)
        {
            if (view != null && view.Count > 0)
            {
                foreach (object o in view)
                {
                    if (predicate(searchText, FormatValue(o)))
                    {
                        return o;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// A simple helper method to clear the view and ensure that a view 
        /// object is always present and not null.
        /// </summary>
        private void ClearView()
        {
            if (_view == null)
            {
                _view = new ObservableCollection<object>();
            }
            else
            {
                _view.Clear();
            }
        }

        /// <summary>
        /// Walks through the items enumeration. Performance is not going to be 
        /// perfect with the current implementation.
        /// </summary>
        private void RefreshView()
        {
            if (_items == null && this.ProvideDataSource == null)
            {
                ClearView();
                return;
            }

            // Cache the current text value
            string text = Text ?? string.Empty;

            // Determine if any filtering mode is on
            bool stringFiltering = TextFilter != null;
            bool objectFiltering = FilterMode == AutoCompleteFilterMode.Custom && TextFilter == null;

            if (ProvideDataSource != null)
            {
                ProvideDataSourceEventArgs args = new ProvideDataSourceEventArgs();
                args.SearchString = SearchText;

                ProvideDataSource(this, args);
            }
            else
            {
                // TODO: remove?
                int view_index = 0;
                int view_count = _view.Count;
                List<object> items = _items;
                foreach (object item in items)
                {
                    bool inResults = !(stringFiltering || objectFiltering);
                    if (!inResults)
                    {
                        inResults = stringFiltering
                                        ? TextFilter(SearchText, FormatValue(item))
                                        : ItemFilter(SearchText, item);
                    }

                    if (view_count > view_index && inResults && _view[view_index] == item)
                    {
                        // Item is still in the view
                        view_index++;
                    }
                    else if (inResults)
                    {
                        // Insert the item
                        if (view_count > view_index && _view[view_index] != item)
                        {
                            // Replace item
                            // Unfortunately replacing via index throws a fatal 
                            // exception: View[view_index] = item;
                            // Cost: O(n) vs O(1)
                            _view.RemoveAt(view_index);
                            _view.Insert(view_index, item);
                            view_index++;
                        }
                        else
                        {
                            // Add the item
                            if (view_index == view_count)
                            {
                                // Constant time is preferred (Add).
                                _view.Add(item);
                            }
                            else
                            {
                                _view.Insert(view_index, item);
                            }
                            view_index++;
                            view_count++;
                        }
                    }
                    else if (view_count > view_index && _view[view_index] == item)
                    {
                        // Remove the item
                        _view.RemoveAt(view_index);
                        view_count--;
                    }

                    // TODO: think if its proper place
                    if (view_count >= MaxSuggestionsNumber && view_index >= MaxSuggestionsNumber)
                    {
                        break;
                    }
                }

                if (view_count == 0)
                {
                    // cut first word from the beginning
                    for (int i=0; i<SearchText.Length; i++)
                    {
                        if (IsBlankChar(SearchText[i]))
                        {
                            while (i < (SearchText.Length - 1) && IsBlankChar(SearchText[++i])) ;

                            if (i == SearchText.Length - 1)
                            {
                                EntryStartingPosition = CurrentCursorPosition;
                                SearchText = "";
                            }
                            else
                            {
                                EntryStartingPosition += i;
                                SearchText = SearchText.Substring(i);                                
                            }

                            if (SearchText != String.Empty)
                                RefreshView();
                        }
                    }
                }

                // Clear the evaluator to discard a reference to the last item
                if (_valueBindingEvaluator != null)
                {
                    _valueBindingEvaluator.ClearDataContext();
                }

                if (ItemsPopulateComplete != null)
                {
                    ItemsPopulateComplete(itemSelectorControl, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// Handle any change to the ItemsSource dependency property, update 
        /// the underlying ObservableCollection view, and set the selection 
        /// adapter's ItemsSource to the view if appropriate.
        /// </summary>
        /// <param name="oldValue">The old enumerable reference.</param>
        /// <param name="newValue">The new enumerable reference.</param>
        [SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters", MessageId = "oldValue", Justification = "This makes it easy to add validation or other changes in the future.")]
        private void OnItemsSourceChanged(IEnumerable oldValue, IEnumerable newValue)
        {
            // Remove handler for oldValue.CollectionChanged (if present)
            INotifyCollectionChanged oldValueINotifyCollectionChanged = oldValue as INotifyCollectionChanged;
            if (null != oldValueINotifyCollectionChanged && null != _collectionChangedWeakEventListener)
            {
                _collectionChangedWeakEventListener.Detach();
                _collectionChangedWeakEventListener = null;
            }

            // Add handler for newValue.CollectionChanged (if possible)
            INotifyCollectionChanged newValueINotifyCollectionChanged = newValue as INotifyCollectionChanged;
            if (null != newValueINotifyCollectionChanged)
            {
                _collectionChangedWeakEventListener = new WeakEventListener<MultiselectAutoCompleteBox, object, NotifyCollectionChangedEventArgs>(this);
                _collectionChangedWeakEventListener.OnEventAction = (instance, source, eventArgs) => instance.ItemsSourceCollectionChanged(source, eventArgs);
                _collectionChangedWeakEventListener.OnDetachAction = (weakEventListener) => newValueINotifyCollectionChanged.CollectionChanged -= weakEventListener.OnEvent;
                newValueINotifyCollectionChanged.CollectionChanged += _collectionChangedWeakEventListener.OnEvent;
            }

            // Store a local cached copy of the data
            _items = newValue == null ? null : new List<object>(newValue.Cast<object>().ToList());

            // Clear and set the view on the selection adapter
            ClearView();
            if (SelectionAdapter != null && SelectionAdapter.ItemsSource != _view)
            {
                SelectionAdapter.ItemsSource = _view;
            }
            if (IsDropDownOpen)
            {
                RefreshView();
            }
        }

        /// <summary>
        /// Method that handles the ObservableCollection.CollectionChanged event for the ItemsSource property.
        /// </summary>
        /// <param name="sender">The object that raised the event.</param>
        /// <param name="e">The event data.</param>
        private void ItemsSourceCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            // Update the cache
            if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
            {
                for (int index = 0; index < e.OldItems.Count; index++)
                {
                    _items.RemoveAt(e.OldStartingIndex);
                }
            }
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null && _items.Count >= e.NewStartingIndex)
            {
                for (int index = 0; index < e.NewItems.Count; index++)
                {
                    _items.Insert(e.NewStartingIndex + index, e.NewItems[index]);
                }
            }
            if (e.Action == NotifyCollectionChangedAction.Replace && e.NewItems != null && e.OldItems != null)
            {
                for (int index = 0; index < e.NewItems.Count; index++)
                {
                    _items[e.NewStartingIndex] = e.NewItems[index];
                }
            }

            // Update the view
            if (e.Action == NotifyCollectionChangedAction.Remove || e.Action == NotifyCollectionChangedAction.Replace)
            {
                for (int index = 0; index < e.OldItems.Count; index++)
                {
                    _view.Remove(e.OldItems[index]);
                }
            }

            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                // Significant changes to the underlying data.
                ClearView();
                if (ItemsSource != null)
                {
                    _items = new List<object>(ItemsSource.Cast<object>().ToList());
                }
            }
            
            // Refresh the observable collection used in the selection adapter.
            RefreshView();
        }

        #region Selection Adapter

        /// <summary>
        /// Handles the SelectionChanged event of the selection adapter.
        /// </summary>
        /// <param name="sender">The source object.</param>
        /// <param name="e">The selection changed event data.</param>
        private void OnAdapterSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectedItem = _adapter.SelectedItem;
        }

        /// <summary>
        /// Handles the Commit event on the selection adapter.
        /// </summary>
        /// <param name="sender">The source object.</param>
        /// <param name="e">The event data.</param>
        private void OnAdapterSelectionComplete(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("OnAdapterSelectionComplete()");
            IsDropDownOpen = false;

            // Completion will update the selected value
            UpdateTextCompletion(false);

            // Text should not be selected
            if (TextBox != null)
            {
                TextBox.Select(TextBox.Text.Length, 0);
            }

#if SILVERLIGHT
            Focus();
#else
            // Focus is treated differently in SL and WPF.
            // This forces the textbox to get keyboard focus, in the case where
            // another part of the control may have temporarily received focus.
            if (TextBox != null)
            {
                Keyboard.Focus(TextBox);

                if (IsMultiSelect)
                {
                    // clear selection
                    TextBox.Select(TextBox.Text.Length, 0);
                }
            }
            else
            {
                Focus();
            }
#endif
        }

        /// <summary>
        /// Handles the Cancel event on the selection adapter.
        /// </summary>
        /// <param name="sender">The source object.</param>
        /// <param name="e">The event data.</param>
        private void OnAdapterSelectionCanceled(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("OnAdapterSelectionCanceled()");

            string newTextValue = TextBox.Text.Substring(0, Math.Min(EntryStartingPosition, TextBox.Text.Length)) + SearchText;
            int newCursorPosition = newTextValue.Length;
            if (CurrentCursorPosition < TextBox.Text.Length)
            {
                newTextValue += TextBox.Text.Substring(CurrentCursorPosition,
                                                       TextBox.Text.Length - CurrentCursorPosition);
            }

            UpdateTextValue(newTextValue);

            // Completion will update the selected value
            UpdateTextCompletion(false);

            if (newCursorPosition < Text.Length)
            {
                moveCursorTo = newCursorPosition;
            }
        }

        #endregion

        #region Popup

        /// <summary>
        /// Handles MaxDropDownHeightChanged by re-arranging and updating the 
        /// popup arrangement.
        /// </summary>
        /// <param name="newValue">The new value.</param>
        [SuppressMessage("Microsoft.Usage", "CA1801:ReviewUnusedParameters", MessageId = "newValue", Justification = "This makes it easy to add validation or other changes in the future.")]
        private void OnMaxDropDownHeightChanged(double newValue)
        {
            if (DropDownPopup != null)
            {
                DropDownPopup.MaxDropDownHeight = newValue;
                DropDownPopup.Arrange();
            }
            UpdateVisualState(true);
        }

        /// <summary>
        /// Private method that directly opens the popup, checks the expander 
        /// button, and then fires the Opened event.
        /// </summary>
        /// <param name="oldValue">The old value.</param>
        /// <param name="newValue">The new value.</param>
        private void OpenDropDown(bool oldValue, bool newValue)
        {
            if (DropDownPopup != null)
            {
                DropDownPopup.IsOpen = true;
            }
            _popupHasOpened = true;
#if SILVERLIGHT
            OnDropDownOpened(new RoutedPropertyChangedEventArgs<bool>(oldValue, newValue));
#else
            OnDropDownOpened(new RoutedPropertyChangedEventArgs<bool>(oldValue, newValue, DropDownOpenedEvent));
#endif
        }

        /// <summary>
        /// Private method that directly closes the popup, flips the Checked 
        /// value, and then fires the Closed event.
        /// </summary>
        /// <param name="oldValue">The old value.</param>
        /// <param name="newValue">The new value.</param>
        private void CloseDropDown(bool oldValue, bool newValue)
        {
            Debug.WriteLine("CloseDropDown()");
            if (_popupHasOpened)
            {
                if (SelectionAdapter != null)
                {
                    SelectionAdapter.SelectedItem = null;
                }
                if (DropDownPopup != null)
                {
                    DropDownPopup.IsOpen = false;
                }
#if SILVERLIGHT
                OnDropDownClosed(new RoutedPropertyChangedEventArgs<bool>(oldValue, newValue));
#else
                OnDropDownClosed(new RoutedPropertyChangedEventArgs<bool>(oldValue, newValue, DropDownClosedEvent));
#endif
            }
        }

        #endregion

        /// <summary>
        /// Provides handling for the
        /// <see cref="E:System.Windows.UIElement.KeyDown" /> event.
        /// </summary>
        /// <param name="e">A <see cref="T:System.Windows.Input.KeyEventArgs" />
        /// that contains the event data.</param>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e == null)
            {
                throw new ArgumentNullException("e");
            }

            base.OnKeyDown(e);

            if (e.Handled || !IsEnabled)
            {
                return;
            }

            // The drop down is open, pass along the key event arguments to the
            // selection adapter. If it isn't handled by the adapter's logic,
            // then we handle some simple navigation scenarios for controlling
            // the drop down.
            if (IsDropDownOpen) 
            {
                if (SelectionAdapter != null)
                {
                    SelectionAdapter.HandleKeyDown(e);
                    if (e.Handled)
                    {
                        return;
                    }
                }

                if (e.Key == Key.Escape)
                {
                    OnAdapterSelectionCanceled(this, new RoutedEventArgs());
                    e.Handled = true;
                }
            }
            else
            {
                // Eugene: changed to support multilining
                // The drop down is not open, the Down key will toggle it open.
                if (IsEnabled && !TextBox.IsReadOnly && e.Key == Key.Down)
                {
                    if (!_text.AcceptsReturn || IsCursorOnLastLine())
                    {
                        IsDropDownOpen = true;
                        e.Handled = true;
                    }
                }
            }

            // Standard drop down navigation
            switch (e.Key)
            {
                case Key.F4:
                    IsDropDownOpen = !IsDropDownOpen;
                    e.Handled = true;
                    break;
                
                case Key.Enter:
                    {
                        // Eugene: changed to support multilining
                        if (!_text.AcceptsReturn)
                        {
                            OnAdapterSelectionComplete(this, new RoutedEventArgs());
                            e.Handled = true;
                        }
                        break;
                    }
                default:
                    break;
            }
        }

        protected bool IsCursorOnLastLine()
        {
            string text = TextBox.Text;
            if (text.Length == CurrentCursorPosition)
                return true;

            return text.Substring(CurrentCursorPosition).IndexOf('\n') == -1;
        }

        /// <summary>
        /// Update the visual state of the control.
        /// </summary>
        /// <param name="useTransitions">
        /// A value indicating whether to automatically generate transitions to
        /// the new state, or instantly transition to the new state.
        /// </param>
        void IUpdateVisualState.UpdateVisualState(bool useTransitions)
        {
            UpdateVisualState(useTransitions);
        }

        /// <summary>
        /// Update the current visual state of the button.
        /// </summary>
        /// <param name="useTransitions">
        /// True to use transitions when updating the visual state, false to
        /// snap directly to the new visual state.
        /// </param>
        internal virtual void UpdateVisualState(bool useTransitions)
        {
            // Popup
            VisualStateManager.GoToState(this, IsDropDownOpen ? VisualStates.StatePopupOpened : VisualStates.StatePopupClosed, useTransitions);

            // Handle the Common and Focused states
            Interaction.UpdateVisualStateBase(useTransitions);
        }
    }
}