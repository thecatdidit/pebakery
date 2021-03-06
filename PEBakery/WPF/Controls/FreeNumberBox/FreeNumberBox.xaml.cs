﻿/*
    MIT License (MIT)

    Copyright (c) 2018 Hajin Jang
	
	Permission is hereby granted, free of charge, to any person obtaining a copy
	of this software and associated documentation files (the "Software"), to deal
	in the Software without restriction, including without limitation the rights
	to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
	copies of the Software, and to permit persons to whom the Software is
	furnished to do so, subject to the following conditions:
	
	The above copyright notice and this permission notice shall be included in all
	copies or substantial portions of the Software.
	
	THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
	IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
	FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
	AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
	LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
	OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
	SOFTWARE.
*/

using PEBakery.Helper;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace PEBakery.WPF.Controls
{
    /// <summary>
    /// FreeNumberBox.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class FreeNumberBox : UserControl
    {
        #region Constructor
        public FreeNumberBox()
        {
            InitializeComponent();
        }
        #endregion

        #region Property
        private const decimal DefaultValue = 0;
        public decimal Value
        {
            get { return (decimal)GetValue(ValueProperty); }
            set { SetValue(ValueProperty, value); }
        }

        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register("Value", typeof(decimal), typeof(FreeNumberBox),
            new FrameworkPropertyMetadata(DefaultValue, new PropertyChangedCallback(OnValueChanged), new CoerceValueCallback(CoerceValue)));

        private const decimal DefaultMinimum = 0;
        public decimal Minimum 
        {
            get { return (decimal)GetValue(MinimumProperty);  }
            set { SetValue(MinimumProperty, value); }
        }

        public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register("Minimum", typeof(decimal), typeof(FreeNumberBox),
            new FrameworkPropertyMetadata(DefaultMinimum));

        private const decimal DefaultMaximum = ushort.MaxValue;
        public decimal Maximum
        {
            get { return (decimal)GetValue(MaximumProperty); }
            set { SetValue(MaximumProperty, value); }
        }

        public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register("Maximum", typeof(decimal), typeof(FreeNumberBox),
            new FrameworkPropertyMetadata(DefaultMaximum));

        private const decimal DefaultIncrementUnit = 1;
        public decimal IncrementUnit
        {
            get { return (decimal)GetValue(IncrementUnitProperty); }
            set { SetValue(IncrementUnitProperty, value); }
        }

        public static readonly DependencyProperty IncrementUnitProperty = DependencyProperty.Register("IncrementUnit", typeof(decimal), typeof(FreeNumberBox),
            new FrameworkPropertyMetadata(DefaultIncrementUnit));

        private const int DefaultDecimalPlaces = 0;
        public int DecimalPlaces
        {
            get { return (int)GetValue(DecimalPlacesProperty); }
            set { SetValue(DecimalPlacesProperty, value); }
        }

        public static readonly DependencyProperty DecimalPlacesProperty = DependencyProperty.Register("DecimalPlaces", typeof(int), typeof(FreeNumberBox),
            new FrameworkPropertyMetadata(DefaultDecimalPlaces));
        #endregion

        #region Callbacks
        private static object CoerceValue(DependencyObject element, object value)
        { // Check if (MinValue <= Value <= MaxValue)
            if (element is FreeNumberBox control)
                return LimitDecimalValue(control, (decimal)value);
            else
                return value;
        }

        private static void OnValueChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            FreeNumberBox control = (FreeNumberBox)obj;

            RoutedPropertyChangedEventArgs<decimal> e = new RoutedPropertyChangedEventArgs<decimal>(
                (decimal)args.OldValue, (decimal)args.NewValue, ValueChangedEvent);
            control.OnValueChanged(e);
        }

        public static decimal LimitDecimalValue(FreeNumberBox control, decimal value)
        {
            value = Math.Max(control.Minimum, Math.Min(control.Maximum, value));
            value = decimal.Round(value, control.DecimalPlaces);
            return value;
        }
        #endregion

        #region Control Events
        public static readonly RoutedEvent ValueChangedEvent = EventManager.RegisterRoutedEvent(
            "ValueChanged", RoutingStrategy.Bubble,
            typeof(RoutedPropertyChangedEventHandler<decimal>), typeof(FreeNumberBox));

        public event RoutedPropertyChangedEventHandler<decimal> ValueChanged
        {
            add { AddHandler(ValueChangedEvent, value); }
            remove { RemoveHandler(ValueChangedEvent, value); }
        }

        protected virtual void OnValueChanged(RoutedPropertyChangedEventArgs<decimal> args)
        {
            RaiseEvent(args);
        }
        #endregion

        #region TextBlock Events
        private void TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Aloow only [0-9]+ 
            bool check = true;
            for (int i = 0; i < e.Text.Length; i++)
                check &= char.IsDigit(e.Text[i]);

            if (e.Text.Length == 0)
                check = false;

            e.Handled = !check;

            base.OnPreviewTextInput(e);
        }
        #endregion

        #region Button Events
        private void UpButton_Click(object sender, EventArgs e)
        {
            Value = LimitDecimalValue(this, Value + IncrementUnit);
        }

        private void DownButton_Click(object sender, EventArgs e)
        {
            Value = LimitDecimalValue(this, Value - IncrementUnit);
        }
        #endregion
    }
}
