﻿/**
 * Radegast Metaverse Client
 * Copyright(c) 2009-2014, Radegast Development Team
 * Copyright(c) 2016-2020, Sjofn, LLC
 * All rights reserved.
 *  
 * Radegast is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program.If not, see<https://www.gnu.org/licenses/>.
 */

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.IO;
using OpenMetaverse;
using OpenMetaverse.Imaging;

namespace Radegast
{
    public partial class ImageUploadConsole : RadegastTabControl
    {
        public string FileName, TextureName, TextureDescription;
        public byte[] UploadData;
        public UUID InventoryID, AssetID, TransactionID;
        bool ImageLoaded;
        int OriginalCapsTimeout;

        public ImageUploadConsole()
        {
            InitializeComponent();

            GUI.GuiHelpers.ApplyGuiFixes(this);
        }

        public ImageUploadConsole(RadegastInstance instance)
            : base(instance)
        {
            InitializeComponent();

            Disposed += new EventHandler(ImageUploadConsole_Disposed);
            instance.Netcom.ClientConnected += new EventHandler<EventArgs>(Netcom_ClientConnected);
            instance.Netcom.ClientDisconnected += new EventHandler<DisconnectedEventArgs>(Netcom_ClientDisconnected);
            client.Assets.AssetUploaded += new EventHandler<AssetUploadEventArgs>(Assets_AssetUploaded);
            UpdateButtons();
            OriginalCapsTimeout = client.Settings.CAPS_TIMEOUT;

            GUI.GuiHelpers.ApplyGuiFixes(this);
        }

        void ImageUploadConsole_Disposed(object sender, EventArgs e)
        {
            client.Assets.AssetUploaded -= new EventHandler<AssetUploadEventArgs>(Assets_AssetUploaded);
        }

        void Assets_AssetUploaded(object sender, AssetUploadEventArgs e)
        {
            if (e.Upload.ID == TransactionID)
            {
                if (!e.Upload.Success)
                {
                    TempUploadHandler(false, new InventoryTexture(UUID.Zero));
                }
                else
                {
                    client.Inventory.RequestCreateItem(client.Inventory.FindFolderForType(AssetType.Texture),
                        TextureName, TextureDescription, AssetType.Texture, TransactionID,
                        InventoryType.Texture, PermissionMask.All, TempUploadHandler);
                }
            }
        }

        void Netcom_ClientDisconnected(object sender, DisconnectedEventArgs e)
        {
            if (InvokeRequired)
            {
                if (!instance.MonoRuntime || IsHandleCreated)
                {
                    BeginInvoke(new MethodInvoker(() => Netcom_ClientDisconnected(sender, e)));
                }
                return;
            }

            UpdateButtons();
        }

        void Netcom_ClientConnected(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                if (!instance.MonoRuntime || IsHandleCreated)
                {
                    BeginInvoke(new MethodInvoker(() => Netcom_ClientConnected(sender, e)));
                }
                return;
            }

            UpdateButtons();
        }

        private bool IsPowerOfTwo(uint n)
        {
            return (n & (n - 1)) == 0 && n != 0;
        }

        private int ClosestPowerOwTwo(int n)
        {
            int res = 1;

            while (res < n)
            {
                res <<= 1;
            }

            return res > 1 ? res / 2 : 1;
        }

        public void LoadImage(string fname)
        {
            FileName = fname;

            if (string.IsNullOrEmpty(FileName))
                return;

            txtStatus.AppendText("Loading..." + Environment.NewLine);

            string extension = Path.GetExtension(FileName).ToLower();

            try
            {
                Bitmap bitmap = null;
                switch (extension)
                {
                    case ".jp2":
                    case ".j2c":
                        // Upload JPEG2000 images untouched
                        UploadData = File.ReadAllBytes(FileName);

                        using (var reader = new OpenJpegDotNet.IO.Reader(UploadData))
                        {
                            if (reader.ReadHeader())
                            {
                                bitmap = reader.DecodeToBitmap();
                            }
                        }

                        txtStatus.AppendText("Loaded raw JPEG2000 data " + FileName + Environment.NewLine);
                        break;
                    case ".tga":
                        bitmap = LoadTGAClass.LoadTGA(FileName);
                        break;
                    default:
                        bitmap = Image.FromFile(FileName) as Bitmap;
                        break;
                }

                if (bitmap == null)
                {
                    txtStatus.AppendText("Failed to load image " + FileName + Environment.NewLine);
                    return;
                }

                txtStatus.AppendText("Loaded image " + FileName + Environment.NewLine);

                int width = bitmap.Width;
                int height = bitmap.Height;

                // Handle resizing to prevent excessively large images and irregular dimensions
                if (!IsPowerOfTwo((uint)width) || !IsPowerOfTwo((uint)height) || width > 1024 || height > 1024)
                {
                    txtStatus.AppendText("Image has irregular dimensions " + width + "x" + height + Environment.NewLine);

                    width = ClosestPowerOwTwo(width);
                    height = ClosestPowerOwTwo(height);

                    width = width > 1024 ? 1024 : width;
                    height = height > 1024 ? 1024 : height;

                    txtStatus.AppendText("Resizing to " + width + "x" + height + Environment.NewLine);

                    Bitmap resized = new Bitmap(width, height, bitmap.PixelFormat);
                    Graphics graphics = Graphics.FromImage(resized);

                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    graphics.InterpolationMode =
                       System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(bitmap, 0, 0, width, height);

                    bitmap.Dispose();
                    bitmap = resized;
                }

                txtStatus.AppendText("Encoding image..." + Environment.NewLine);

                using (var writer = new OpenJpegDotNet.IO.Writer(bitmap))
                {
                    var cp = new OpenJpegDotNet.CompressionParameters();
                    OpenJpegDotNet.OpenJpeg.SetDefaultEncoderParameters(cp);
                    cp.CodingParameterDistortionAllocation = 1;
                    if (chkLossless.Checked) {
                        cp.TcpNumLayers = 1;
                        cp.TcpRates[0] = 0;
                    } else {
                        cp.TcpNumLayers = 5;
                        cp.TcpRates[0] = 1920;
                        cp.TcpRates[1] = 480;
                        cp.TcpRates[2] = 120;
                        cp.TcpRates[3] = 30;
                        cp.TcpRates[4] = 10;
                        cp.Irreversible = true;
                        cp.TcpMCT = 1;
                    }
                    writer.SetupEncoderParameters(cp);
                    UploadData = writer.Encode();
                }

                txtStatus.AppendText("Finished encoding." + Environment.NewLine);
                ImageLoaded = true;
                UpdateButtons();
                txtAssetID.Text = UUID.Zero.ToString();

                pbPreview.Image = bitmap;
                lblSize.Text = string.Format("{0}x{1} {2} KB", bitmap.Width, bitmap.Height, Math.Round((double)UploadData.Length / 1024.0d, 2));
            }
            catch (Exception ex)
            {
                UploadData = null;
                btnSave.Enabled = false;
                btnUpload.Enabled = false;
                txtStatus.AppendText(string.Format("Failed to load the image:" + Environment.NewLine 
                    + "{0}" + Environment.NewLine, ex.Message));
            }
        }

        void UpdateButtons()
        {
            btnLoad.Enabled = true;
            btnSave.Enabled = ImageLoaded;
            btnUpload.Enabled = ImageLoaded && client.Network.Connected;
        }


        private void btnLoad_Click(object sender, EventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                Filter =
                    "Image Files (*.jp2,*.j2c,*.jpg,*.jpeg,*.gif,*.png,*.bmp,*.tga,*.tif,*.tiff,*.ico,*.wmf,*.emf)|" +
                    "*.jp2;*.j2c;*.jpg;*.jpeg;*.gif;*.png;*.bmp;*.tga;*.tif;*.tiff;*.ico;*.wmf;*.emf|" +
                    "All files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                LoadImage(dialog.FileName);
            }
        }

        private void btnSave_Click(object sender, EventArgs e)
        {

            SaveFileDialog dlg = new SaveFileDialog
            {
                AddExtension = true,
                RestoreDirectory = true,
                Title = "Save image as...",
                Filter =
                    "PNG (*.png)|*.png|Targa (*.tga)|*.tga|Jpeg2000 (*.j2c)|*.j2c|Jpeg (*.jpg)|*.jpg|Bitmap (*.bmp)|*.bmp"
            };



            if (dlg.ShowDialog() == DialogResult.OK)
            {
                int type = dlg.FilterIndex;
                if (type == 3)
                { // jpeg2000
                    File.WriteAllBytes(dlg.FileName, UploadData);
                }
                else if (type == 2)
                { // targa
                    using (var reader = new OpenJpegDotNet.IO.Reader(UploadData))
                    {
                        if (reader.ReadHeader())
                        {
                            File.WriteAllBytes(dlg.FileName, reader.Decode().ToTarga().Bytes);
                        }
                    }
                }
                else if (type == 1)
                { // png
                    pbPreview.Image.Save(dlg.FileName, ImageFormat.Png);
                }
                else if (type == 4)
                { // jpg
                    pbPreview.Image.Save(dlg.FileName, ImageFormat.Jpeg);
                }
                else
                { // BMP
                    pbPreview.Image.Save(dlg.FileName, ImageFormat.Bmp);
                }
            }

            dlg.Dispose();
        }

        private void chkLossless_Click(object sender, EventArgs e)
        {
            LoadImage(FileName);
        }

        private void TempUploadHandler(bool success, InventoryItem item)
        {
            if (InvokeRequired)
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new MethodInvoker(() => TempUploadHandler(success, item)));
                }
                return;
            }

            InventoryID = item.UUID;

            UpdateButtons();
            txtAssetID.Text = AssetID.ToString();

            if (!success)
            {
                txtStatus.AppendText("Upload failed." + Environment.NewLine);
                return;
            }

            txtStatus.AppendText("Upload success." + Environment.NewLine);
            txtStatus.AppendText("New image ID: " + AssetID + Environment.NewLine);
        }

        private void UploadHandler(bool success, string status, UUID itemID, UUID assetID)
        {
            if (InvokeRequired)
            {
                if (IsHandleCreated)
                {
                    BeginInvoke(new MethodInvoker(() => UploadHandler(success, status, itemID, assetID)));
                }
                return;
            }

            client.Settings.CAPS_TIMEOUT = OriginalCapsTimeout;

            AssetID = assetID;
            InventoryID = itemID;

            UpdateButtons();
            txtAssetID.Text = AssetID.ToString();

            if (!success)
            {
                txtStatus.AppendText("Upload failed: " + status + Environment.NewLine);
                return;
            }

            txtStatus.AppendText("Upload success." + Environment.NewLine);
            txtStatus.AppendText("New image ID: " + AssetID + Environment.NewLine);
        }

        private void btnUpload_Click(object sender, EventArgs e)
        {
            txtStatus.AppendText("Uploading..." + Environment.NewLine);
            btnLoad.Enabled = false;
            btnUpload.Enabled = false;
            AssetID = InventoryID = UUID.Zero;

            TextureName = Path.GetFileNameWithoutExtension(FileName);
            TextureDescription = $"Uploaded with Radegast on {DateTime.Now.ToLongDateString()}";

            Permissions perms = new Permissions {EveryoneMask = PermissionMask.All, NextOwnerMask = PermissionMask.All};


            client.Settings.CAPS_TIMEOUT = 180 * 1000;
            client.Inventory.RequestCreateItemFromAsset(UploadData, TextureName, TextureDescription, 
                AssetType.Texture, InventoryType.Texture,
                client.Inventory.FindFolderForType(AssetType.Texture), perms, UploadHandler);
        }
    }
}
