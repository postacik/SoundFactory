using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Collections.ObjectModel;
using CSCore.Codecs;
using CSCore.CoreAudioAPI;
using CSCore.SoundOut;
using CSCore.Streams;

namespace SoundFactory
{
    public partial class Form1 : Form
    {
        private readonly ObservableCollection<MMDevice> mDevices = new ObservableCollection<MMDevice>();
        private MMDeviceCollection mOutputDevices;
        public Form1()
        {
            InitializeComponent();
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            //Find sound capture devices and fill the cmbInput combo
            MMDeviceEnumerator deviceEnum = new MMDeviceEnumerator();
            //Find sound render devices and fill the cmbOutput combo
            MMDevice activeDevice = deviceEnum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            mOutputDevices = deviceEnum.EnumAudioEndpoints(DataFlow.Render, DeviceState.Active);
            foreach (MMDevice device in mOutputDevices)
            {
                comboBox1.Items.Add(device);
                if (device.DeviceID == activeDevice.DeviceID) comboBox1.SelectedIndex = comboBox1.Items.Count - 1;
            }
            comboBox1.DisplayMember = "FriendlyName";
        }

        private void btnOpen_Click(object sender, EventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = CodecFactory.SupportedFilesFilterEn
            };
            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    mEditor.OpenWaveFile(openFileDialog.FileName, (MMDevice)comboBox1.SelectedItem);
                    trackVolume.Value = mEditor.Player.Volume;
                    mEditor.Focus();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not open file: " + ex.Message);
                }
            }
        }

        private void trackVolume_ValueChanged(object sender, EventArgs e)
        {
            mEditor.Player.Volume = trackVolume.Value;
        }
    }
}
