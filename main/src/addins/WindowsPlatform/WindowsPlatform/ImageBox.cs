//
// ImageBox.cs
//
// Author:
//       Vsevolod Kukol <sevo@xamarin.com>
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
using System.Windows;
using Xwt.Drawing;

namespace WindowsPlatform
{
	public class ImageBox : System.Windows.Controls.UserControl
	{
		public static readonly DependencyProperty ImageProperty =
			DependencyProperty.Register ("Image", typeof (Image), typeof (ImageBox), new FrameworkPropertyMetadata () { AffectsMeasure = true, AffectsRender = true });

		public ImageBox ()
		{
			Image = null;
		}

		public ImageBox (Image image)
		{
			Image = image;
		}

		public ImageBox (string iconId, Gtk.IconSize size)
		{
			Image = MonoDevelop.Ide.ImageService.GetIcon (iconId, size);
		}

		protected override void OnRender (System.Windows.Media.DrawingContext dc)
		{
			var image = Image;
			if (image != null) {
				var x = (RenderSize.Width - image.Size.Width) / 2;
				var y = (RenderSize.Height - image.Size.Height) / 2;
				MonoDevelop.Platform.WindowsPlatform.WPFToolkit.RenderImage (this, dc, image, x, y);
			}
		}

		public Image Image
		{
			get { return (Image)GetValue (ImageProperty); }
			set { SetValue (ImageProperty, value); }
		}

		protected override Size MeasureOverride (Size constraint)
		{
			var image = Image;
			if (image != null)
				return new Size (image.Size.Width, image.Size.Height);
			else
				return new Size (0, 0);
		}
	}
}

