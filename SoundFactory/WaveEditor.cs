using System;
using System.Drawing;
using System.Windows.Forms;
using System.Threading;
using CSCore;
using CSCore.Codecs;
using CSCore.CoreAudioAPI;
using CSCore.Streams;
using System.IO;

namespace SoundFactory
{
    public partial class WaveEditor : UserControl
    {
        private ISampleSource mDrawSource;
        private ISampleSource mPlaySource;
        private IWaveSource mWaveSource;
        private readonly MusicPlayer mMusicPlayer = new MusicPlayer();

        private long mStreamStart;
        private long mStreamEnd;

        private int mProgressStatus = 0;
        // Boolean for whether the .WAV should draw or not. So that the control doesnt draw the .WAV until after it is read 
        private bool mDrawWave = false;
        private string mFilename;
        // Each pixel value (X direction) represents this many samples in the wavefile 
        // Starting value is based on control width so that the .WAV will cover the entire width. 
        private long mSamplesPerPixel = 0;
        // This is the value of the previous mouse move event 
        private int mPrevX = 0;
        // This boolean value gets rid of the currently active region and also refreshes the wave 
        private bool mResetRegion;
        // Boolean for whether the Alt key is down 
        private bool mAltKeyDown = false;
        // Boolean for whether the Ctrl key is down 
        private bool mCtrlKeyDown = false;
        // Boolean for whether the Shift key is down 
        private bool mShiftKeyDown = false;
        // Offset from the beginning of the wave for where to start drawing 
        private long mDrawingStartOffset = 0;
        private int mLastDrawnPixel = 0;
        private int mCursorPosX = 0;
        private long mCursorPosSample = 0;
        private bool mDrawing = false;

        private bool mSelecting = false;
        //Variables for bitmap drawing
        private int mPrevWidth = 0;
        private int mPrevHeight = 0;
        private long mPrevOffset = 0;
        private long mPrevSamplesPerPixel = 0;

        private Bitmap mBitmap = null;
        //Variables for selection
        private int mSelectionStartX = 0;
        private int mSelectionEndX = 0;
        private long mSelectionStartSample = 0;

        private long mSelectionEndSample = 0;
        //Variables for drawing optimization
        private int mThresholdSample = 64;

        private float[] mOptimizedArray;
        private int mLastPlayCursorX = -1;
        private string mRawFileName;
        private FileStream mRawDrawReader;
        private FileStream mRawPlayReader;
        private MMDevice mDevice;

        public WaveEditor()
        {
            InitializeComponent();
            KeyUp += WaveControl_KeyUp;
            KeyDown += WaveControl_KeyDown;
            //KeyPress += WaveEditor_KeyPress;
            MouseUp += WaveControl_MouseUp;
            MouseMove += WaveControl_MouseMove;
            MouseDown += WaveControl_MouseDown;
            Paint += WaveControl_Paint;
            hrScroll.Scroll += hrScroll_Scroll;
            // Sets up double buffering IMPORTANT !!!
            SetStyle(System.Windows.Forms.ControlStyles.UserPaint | System.Windows.Forms.ControlStyles.AllPaintingInWmPaint | System.Windows.Forms.ControlStyles.DoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        private void WaveEditor_KeyPress(object sender, KeyPressEventArgs e)
        {
        }

        public string Filename
        {
            get { return mFilename; }
        }

        private long GetCorrectedSamplesPerPixel(long value)
        {
            long ret = value;
            if (ret < 1)
            {
                ret = 1;
            }
            else
            {
                int pow = (int)Math.Ceiling(Math.Log(ret) / Math.Log(2));
                if (pow > 30)
                    pow = 30;
                ret = (int)Math.Pow(2, pow);
            }
            return ret;
        }

        public int Zoom
        {
            set
            {
                SamplesPerPixel = value;
                Refresh();
            }
        }

        private long SamplesPerPixel
        {
            //If Not mDrawWave Then Exit Property
            //Eğer seçili alan ekrana sığmıyorsa seçili alanın başlangıç noktasından başla
            //Sığıyorsa seçili alanı ortala
            //Cursor pozisyonunu ortala
            //küsürat varsa doğru yere çek
            get { return mSamplesPerPixel; }
            set
            {
                mSamplesPerPixel = Math.Min(GetCorrectedSamplesPerPixel(value), GetCorrectedSamplesPerPixel((int)(mDrawSource.Length / CWidth)));
                int samplesPerPage = (int)(mSamplesPerPixel * CWidth);
                hrScroll.Maximum = (int)(mDrawSource.Length); 
                if (hrScroll.Maximum > 0)
                {
                    hrScroll.LargeChange = samplesPerPage;
                    hrScroll.SmallChange = samplesPerPage / 100;
                    if (hrScroll.SmallChange == 0)
                        hrScroll.SmallChange = 1;
                    if (SelectionMade)
                    {
                        int selectedPixels = (int)((mSelectionEndSample - mSelectionStartSample) / mSamplesPerPixel);
                        if (selectedPixels > CWidth)
                        {
                            mDrawingStartOffset = mSelectionStartSample;
                        }
                        else
                        {
                            mDrawingStartOffset = mSelectionStartSample - (((mSamplesPerPixel * CWidth) - (mSelectionEndSample - mSelectionStartSample)) / 2);
                        }
                    }
                    else
                    {
                        mDrawingStartOffset = mCursorPosSample - (mSamplesPerPixel * (CWidth / 2));
                    }
                    mDrawingStartOffset = Math.Max(0, mDrawingStartOffset);
                    hrScroll.Value = (int)(mDrawingStartOffset);
                    if (hrScroll.LargeChange >= hrScroll.Maximum)
                    {
                        mDrawingStartOffset = 0;
                    }
                }
                else
                {
                    hrScroll.Value = 0;
                    mDrawingStartOffset = 0;
                }
                InvokeOnUiThreadIfRequired(() => lblSamplesPerPixel.Text = "1:" + mSamplesPerPixel);
                ArrangeSelectionPositions();
            }
        }


        private void CreateOptimizedArray()
        {
            long offset = 0;
            long numSamples = mDrawSource.Length;
            int x = 0;
            int y = 0;
            //Nth item holds maxVal, N+1th item holds minVal so allocate an array of double size
            mOptimizedArray = new float[((numSamples / mThresholdSample) + 1) * 2];
            float[] data = new float[mThresholdSample];
            int samplesRead = 1;
            mDrawSource.Position = 0;
            string rawFilePath = System.Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\SoundFactory\";
            if (!Directory.Exists(rawFilePath)) Directory.CreateDirectory(rawFilePath);
            mRawFileName = rawFilePath + Guid.NewGuid().ToString() + ".raw";
            FileStream rawFile = new FileStream(mRawFileName, FileMode.Create, FileAccess.ReadWrite);
            BinaryWriter bin = new BinaryWriter(rawFile);
            while (offset < numSamples && samplesRead > 0)
            {
                samplesRead = mDrawSource.Read(data, 0, mThresholdSample);
                if (samplesRead > 0) //for some files file length is wrong so samplesRead may become 0 even if we did not come to the end of the file
                {
                    for (int i = 0; i < samplesRead; i++)
                    {
                        bin.Write(data[i]);
                    }

                    float maxVal = -1;
                    float minVal = 1;
                    // finds the max & min peaks for this pixel 
                    for (x = 0; x < samplesRead; x++)
                    {
                        maxVal = Math.Max(maxVal, data[x]);
                        minVal = Math.Min(minVal, data[x]);
                    }
                    mOptimizedArray[y] = minVal;
                    mOptimizedArray[y + 1] = maxVal;
                    y += 2;
                    offset += samplesRead;
                    mProgressStatus = (int)(((float)offset / numSamples) * 100);
                    if (progress.Value != mProgressStatus)
                    {
                        InvokeOnUiThreadIfRequired(() => progress.Value = mProgressStatus);
                    }                                    
                }
            }
            rawFile.Close();
        }

        private void OpenWaveFileThread()
        {
            CreateOptimizedArray();
            mDrawWave = true;

            WaveFormat fmt = mDrawSource.WaveFormat;
            mRawDrawReader = new FileStream(mRawFileName, FileMode.Open, FileAccess.Read);
            mDrawSource = new RawDataReader(mRawDrawReader, fmt).ToSampleSource();

            SamplesPerPixel = (int)(mDrawSource.Length / CWidth);

            mRawPlayReader = new FileStream(mRawFileName, FileMode.Open, FileAccess.Read);
            mPlaySource = new RawDataReader(mRawPlayReader, fmt).ToSampleSource();
            PeakMeter peakMeter = new PeakMeter(mPlaySource) { Interval = 50 };
            peakMeter.PeakCalculated += PeakMeter_PeakCalculated;
            mWaveSource = peakMeter.ToWaveSource(16);
            mMusicPlayer.Open(mWaveSource, mDevice);

            InvokeOnUiThreadIfRequired(() =>
            {
                progress.Value = 0;
                cursorTimer.Enabled = true;
                playTimer.Enabled = true;
                hrScroll.Enabled = true;
                Refresh();
            });            
        }

        public void CloseWaveFile()
        {
            StopPlaying();
            mFilename = "";
            progress.Visible = true;
            hrScroll.Enabled = false;
            cursorTimer.Enabled = false;
            playTimer.Enabled = false;
            mDrawWave = false;
            mSamplesPerPixel = 0;
            mSelectionStartX = 0;
            mSelectionEndX = 0;
            mSelectionStartSample = 0;
            mSelectionEndSample = 0;
            mPrevX = 0;
            mDrawingStartOffset = 0;
            CursorPositionX = 0;
            mPrevWidth = 0;
            mPrevHeight = 0;
            mPrevOffset = 0;
            mPrevSamplesPerPixel = 0;
            if (mWaveSource != null)
                mWaveSource.Dispose();
            mWaveSource = null;
            if (mDrawSource != null)
                mDrawSource.Dispose();
            mDrawSource = null;
            if (mBitmap != null)
                mBitmap.Dispose();
            mBitmap = null;
            String timeZero = "00:00:00.00";
            lblCursorPos.Text = timeZero;
            lblSelectStartPos.Text = timeZero;
            lblSelectEndPos.Text = timeZero;
            lblSelectLength.Text = timeZero;
            if (mRawDrawReader != null) mRawDrawReader.Dispose();
            if (mRawPlayReader != null) mRawPlayReader.Dispose();
            if (File.Exists(mRawFileName)) File.Delete(mRawFileName);
        }

        public void OpenWaveFile(string fileName, MMDevice device)
        {
            CloseWaveFile();
            mDrawSource = CodecFactory.Instance.GetCodec(fileName).ToSampleSource().ToMono();
            mDevice = device;

            mFilename = fileName;
            Thread fileOpenThread = new Thread(new ThreadStart(OpenWaveFileThread));
            fileOpenThread.Start();
        }

        private void PeakMeter_PeakCalculated(object sender, PeakEventArgs e)
        {
            InvokeOnUiThreadIfRequired(() => progress.Value = (int)(e.PeakValue * 100));            
        }

        protected override void OnMouseWheel(MouseEventArgs args)
        {
            if (args.Delta * SystemInformation.MouseWheelScrollLines / 120 > 0)
            {
                if (mCtrlKeyDown) ZoomIn();
                else
                {

                    ScrollView((int)(mDrawingStartOffset + hrScroll.LargeChange));
                }
            }
            else
            {
                if (mCtrlKeyDown) ZoomOut();
                else
                {
                    ScrollView((int)(mDrawingStartOffset - hrScroll.LargeChange));
                }
            }
            Refresh();
        }

        private void WaveControl_Paint(object sender, System.Windows.Forms.PaintEventArgs e)
        {
            PaintMe(e.Graphics);
        }

        private void PaintMe(Graphics grfx)
        {
            if (mDrawWave)
            {
                mDrawing = true;
                Pen pen = new Pen(Color.Black);
                DrawWave(grfx, pen);
                mDrawing = false;
            }
        }

        private int CHeight
        {
            get { return ClientSize.Height - hrScroll.Height - status.Height; }
        }

        private int CWidth
        {
            get { return ClientSize.Width; }
        }

        private void DrawWave(Graphics grfx, Pen pen)
        {
            int h = CHeight;
            int w = CWidth;
            long numSamples = mDrawSource.Length;


            if ((mPrevWidth != w) | (mPrevHeight != h) | (mPrevOffset != mDrawingStartOffset) | (mPrevSamplesPerPixel != mSamplesPerPixel))
            {
                mPrevWidth = w;
                mPrevHeight = h;
                mPrevOffset = mDrawingStartOffset;
                mPrevSamplesPerPixel = mSamplesPerPixel;

                if (mBitmap == null || ((mBitmap.Width != w) | (mBitmap.Height != h)))
                {
                    if (mBitmap != null)
                        mBitmap.Dispose();
                    mBitmap = new Bitmap(w, h);
                }
                Graphics canvas = Graphics.FromImage(mBitmap);
                canvas.Clear(BackColor);
                
                int prevX = 0;
                int prevMaxY = 0;
                int prevMinY = 0;
                float maxVal = 0;
                float minVal = 0;

                int i = 0;

                // index is how far to offset into the data array 
                long index = mDrawingStartOffset;
                int maxSampleToShow = (int)Math.Min((mSamplesPerPixel * w) + mDrawingStartOffset, numSamples);

                int sampleCount = 0;
                int offsetIndex = 0;
                if (mSamplesPerPixel > mThresholdSample)
                {
                    sampleCount = (int)(mSamplesPerPixel / mThresholdSample) * 2;
                    offsetIndex = (int)Math.Floor((decimal)(mDrawingStartOffset / mThresholdSample)) * 2;
                }
                float[] data = new float[mSamplesPerPixel];
                mDrawSource.Position = mDrawingStartOffset;

                int x = 0;
                while (index < maxSampleToShow)
                {
                    maxVal = -1;
                    minVal = 1;
                    int samplesRead = 0;
                    if (mSamplesPerPixel > mThresholdSample)
                    {
                        int startIndex = offsetIndex + (i * sampleCount);
                        int endIndex = Math.Min(mOptimizedArray.Length - 1, startIndex + sampleCount - 1);
                        for (x = startIndex; x <= endIndex; x++)
                        {
                            maxVal = Math.Max(maxVal, mOptimizedArray[x]);
                            minVal = Math.Min(minVal, mOptimizedArray[x]);
                        }
                    }
                    else
                    {
                        samplesRead = mDrawSource.Read(data,0,data.Length);
                        // finds the max & min peaks for this pixel 
                        for (x = 0; x < samplesRead; x++)
                        {
                            maxVal = Math.Max(maxVal, data[x]);
                            minVal = Math.Min(minVal, data[x]);
                        }
                    }
                    //8-bit samples are stored as unsigned bytes, ranging from 0 to 255. 
                    //16-bit samples are stored as 2's-complement signed integers, ranging from -32768 to 32767. 
                    // scales based on height of window 
                    int scaledMinVal = (int)(((minVal + 1) * h) / 2);
                    int scaledMaxVal = (int)(((maxVal + 1) * h) / 2);

                    // if the max/min are the same, then draw a line from the previous position, 
                    // otherwise we will not see anything 
                    if (scaledMinVal == scaledMaxVal)
                    {
                        if (prevMaxY != 0)
                        {
                            canvas.DrawLine(pen, prevX, prevMaxY, i, scaledMaxVal);
                        }
                    }
                    else
                    {
                        if (i > prevX)
                        {
                            if (prevMaxY < scaledMinVal)
                            {
                                canvas.DrawLine(pen, prevX, prevMaxY, i, scaledMinVal);
                            }
                            else
                            {
                                if (prevMinY > scaledMaxVal)
                                {
                                    canvas.DrawLine(pen, prevX, prevMinY, i, scaledMaxVal);
                                }
                            }
                        }
                        canvas.DrawLine(pen, i, scaledMinVal, i, scaledMaxVal);
                    }

                    prevX = i;
                    prevMaxY = scaledMaxVal;
                    prevMinY = scaledMinVal;

                    i += 1;
                    index = (i * mSamplesPerPixel) + mDrawingStartOffset;
                }
                mLastDrawnPixel = i - 1;
                //Fill non wave area with different color
                SolidBrush br = new SolidBrush(Color.LightGray);
                canvas.FillRectangle(br, i, 0, w - i + 1, h);
                
                //Adjust scrollbar change values if width of page has changed
                int samplesPerPage = (int)(mSamplesPerPixel * w);
                if (hrScroll.LargeChange != samplesPerPage)
                {
                    hrScroll.LargeChange = samplesPerPage;
                    hrScroll.SmallChange = samplesPerPage / 100;
                    if (hrScroll.SmallChange == 0)
                        hrScroll.SmallChange = 1;
                }
            }
            grfx.DrawImage(mBitmap, 0, 0);
            if (mLastPlayCursorX >= 0 && mLastPlayCursorX < mLastDrawnPixel)
            {
                grfx.DrawLine(new Pen(Color.Blue), mLastPlayCursorX, 0, mLastPlayCursorX, h);
            }
            grfx.DrawLine(pen, 0, (int)(CHeight / 2), CWidth, (int)(CHeight / 2));
            int regionStartX = Math.Max(0, Math.Min(SelectionStartX, SelectionEndX));
            int regionEndX = Math.Min(mLastDrawnPixel, Math.Max(SelectionStartX, SelectionEndX));
            if (regionStartX >= 0 && regionEndX >= 0)
            {
                SolidBrush brs = new SolidBrush(Color.FromArgb(128, 0, 0, 0));
                grfx.FillRectangle(brs, regionStartX, 0, regionEndX - regionStartX + 1, CHeight);
            }
        }

        private void ZoomIn()
        {
            SamplesPerPixel /= 2;
        }

        private void ZoomOut()
        {
            SamplesPerPixel *= 2;
        }

        private void ZoomToRegion()
        {
            if (SelectionMade)
            {
                mDrawingStartOffset = mSelectionStartSample;
                long numSamplesToShow = mSelectionEndSample - mSelectionStartSample;
                SamplesPerPixel = numSamplesToShow / CWidth;
                mResetRegion = true;
            }
        }

        private void ZoomOutFull()
        {
            SamplesPerPixel = (int)(mDrawSource.Length / CWidth);
            mDrawingStartOffset = 0;

            mResetRegion = true;
        }

        private void ScrollMe(int newXValue)
        {
            mDrawingStartOffset -= (newXValue - mPrevX) * mSamplesPerPixel;

            if (mDrawingStartOffset < 0)
            {
                mDrawingStartOffset = 0;
            }
        }

        private int SamplesPerMilisecond
        {
            get
            {
                if (mDrawWave)
                {
                    int samplesPerSecond = mDrawSource.WaveFormat.BytesPerSecond / mDrawSource.WaveFormat.BytesPerSample;
                    return  samplesPerSecond / 1000;
                }
                else
                {
                    return 1;
                }
            }
        }

        private string PositionTimeString(long pos)
        {
            double miliSeconds = pos / SamplesPerMilisecond;
            DateTime dt = new DateTime(1, 1, 1);
            dt = dt.AddMilliseconds(miliSeconds);
            return (dt.ToString("HH:mm:ss.ff"));
        }

        private int CursorPositionX
        {
            get { return mCursorPosX; }
            set
            {
                mCursorPosX = value;
                mCursorPosSample = mDrawingStartOffset + (mCursorPosX * mSamplesPerPixel);
                lblCursorPos.Text = PositionTimeString(mCursorPosSample);
            }
        }

        private long CursorPositionSample
        {
            get { return mCursorPosSample; }
            set
            {
                mCursorPosSample = value;
                mCursorPosX = (int)((mCursorPosSample - mDrawingStartOffset) / mSamplesPerPixel);
                lblCursorPos.Text = PositionTimeString(mCursorPosSample);
            }
        }


        private void DisplaySelection()
        {
            int startX = mSelectionStartX;
            int endX = mSelectionEndX;
            if (mSelectionStartX > mSelectionEndX)
            {
                startX = mSelectionEndX;
                endX = mSelectionStartX;
            }
            mSelectionStartSample = mDrawingStartOffset + (startX * mSamplesPerPixel);
            lblSelectStartPos.Text = PositionTimeString(mSelectionStartSample);
            mSelectionEndSample = mDrawingStartOffset + (endX * mSamplesPerPixel);
            lblSelectEndPos.Text = PositionTimeString(mSelectionEndSample);
            lblSelectLength.Text = PositionTimeString(mSelectionEndSample - mSelectionStartSample);
        }

        private int SelectionStartX
        {
            get { return mSelectionStartX; }
            set
            {
                mSelectionStartX = Math.Max(0, value);
                DisplaySelection();
            }
        }


        private int SelectionEndX
        {
            get { return mSelectionEndX; }
            set
            {
                mSelectionEndX = Math.Max(0, value);
                DisplaySelection();
            }
        }

        private void ArrangeSelectionPositions()
        {
            if (SelectionMade)
            {
                mSelectionStartX = (int)((mSelectionStartSample - mDrawingStartOffset) / mSamplesPerPixel);
                mSelectionEndX = (int)((mSelectionEndSample - mDrawingStartOffset) / mSamplesPerPixel);
            }
            mCursorPosX = (int)((mCursorPosSample - mDrawingStartOffset) / mSamplesPerPixel);
        }

        private bool SelectionMade
        {
            get { return (SelectionStartX != 0) | (SelectionEndX != 0); }
        }

        private void ResetSelectionX(int x)
        {
            mSelectionStartX = x;
            //Restart here. If mouse moves selection will be made
            mSelectionStartSample = mDrawingStartOffset + (mSelectionStartX * mSamplesPerPixel);
            mSelectionEndX = x;
            mSelectionEndSample = mSelectionStartSample;
            lblSelectStartPos.Text = PositionTimeString(0);
            lblSelectEndPos.Text = PositionTimeString(0);
            lblSelectLength.Text = PositionTimeString(0);
        }

        private void ResetSelectionOffset(int offset)
        {
            mSelectionStartSample = offset;
            mSelectionStartX = (int)((mSelectionStartSample - mDrawingStartOffset) / mSamplesPerPixel);
            mSelectionEndX = mSelectionStartX;
            mSelectionEndSample = mSelectionStartSample;
            lblSelectStartPos.Text = PositionTimeString(0);
            lblSelectEndPos.Text = PositionTimeString(0);
            lblSelectLength.Text = PositionTimeString(0);
        }


        private void WaveControl_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (!mDrawWave) return;

            if (e.Button == MouseButtons.Left)
            {
                if (mAltKeyDown)
                {
                    mPrevX = e.X;
                }
                else
                {
                    mSelecting = true;
                    bool restart = true;
                    if (mShiftKeyDown)
                    {
                        if (SelectionMade)
                        {
                            if (e.X < SelectionStartX + ((SelectionEndX - SelectionStartX) / 2))
                            {
                                SelectionStartX = e.X;
                            }
                            else
                            {
                                SelectionEndX = e.X;
                            }
                            restart = false;
                            Refresh();
                        }
                    }
                    else
                    {
                        if (SelectionMade)
                        {
                            if (SelectionStartX >= 0 && (e.X > SelectionStartX - 3 & e.X < SelectionStartX + 3))
                            {
                                SelectionStartX = SelectionEndX;
                                SelectionEndX = e.X;
                                restart = false;
                            }
                            else
                            {
                                if (SelectionEndX >= 0 && (e.X > SelectionEndX - 3 & e.X < SelectionEndX + 3))
                                {
                                    SelectionEndX = e.X;
                                    restart = false;
                                }
                            }
                        }
                    }
                    if (restart)
                    {
                        ResetSelectionX(e.X);
                        CursorPositionX = Math.Min(e.X, mLastDrawnPixel);
                        Cursor = Cursors.SizeWE;
                        mResetRegion = true;
                    }
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                if (e.Clicks == 2)
                {
                    //ZoomOutFull()
                }
                else
                {
                    if (mCtrlKeyDown)
                    {
                        ZoomToRegion();
                    }
                    else
                    {
                        PlayRegion();
                    }
                }
            }
        }

        private void WaveControl_MouseMove(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            if (!mDrawWave) return;

            if (e.Button == MouseButtons.Left)
            {
                if (mAltKeyDown)
                {
                    //ScrollMe(e.X)
                }
                else
                {
                    SelectionEndX = Math.Min(e.X, mLastDrawnPixel);
                    mResetRegion = false;
                }

                mPrevX = e.X;

                Refresh();
            }
            else
            {
                if (e.Button == MouseButtons.None)
                {
                    if (SelectionMade)
                    {
                        if ((e.X > SelectionStartX - 3 & e.X < SelectionStartX + 3) | (e.X > SelectionEndX - 3 & e.X < SelectionEndX + 3))
                        {
                            Cursor = Cursors.SizeWE;
                        }
                        else
                        {
                            Cursor = Cursors.Default;
                        }
                    }
                    else
                    {
                        Cursor = Cursors.Default;
                    }
                }
            }
        }

        private void WaveControl_MouseUp(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            mSelecting = false;
            if (mResetRegion)
            {
                mSelectionStartX = 0;
                mSelectionEndX = 0;
                Cursor = Cursors.Default;
                Refresh();
            }
            else
            {
                if (SelectionMade)
                {
                    if (mSelectionStartX > mSelectionEndX)
                    {
                        int swap = mSelectionStartX;
                        mSelectionStartX = mSelectionEndX;
                        mSelectionEndX = swap;
                    }
                    CursorPositionX = SelectionStartX;
                }
            }
        }

        private void WaveControl_KeyDown(object sender, System.Windows.Forms.KeyEventArgs e)
        {
            if (e.Alt)
            {
                mAltKeyDown = true;
            }
            if (e.Control)
            {
                mCtrlKeyDown = true;
            }
            if (e.Shift)
            {
                mShiftKeyDown = true;
            }
            if (e.KeyCode == Keys.Space)
            {
                if (mMusicPlayer.PlaybackState == CSCore.SoundOut.PlaybackState.Playing)
                {
                    StopPlaying();
                } else
                {
                    PlayRegion();
                }

            }
        }

        private void WaveControl_KeyUp(object sender, System.Windows.Forms.KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Menu)
                mAltKeyDown = false;
            if (e.KeyCode == Keys.ControlKey)
                mCtrlKeyDown = false;
            if (e.KeyCode == Keys.ShiftKey)
                mShiftKeyDown = false;
        }

        public void StopPlaying()
        {
            mMusicPlayer.Stop();
            mLastPlayCursorX = -1;
            Refresh();
        }

        private void PlayRegion()
        {
            StopPlaying();

            if (mDrawWave)
            {
                if (mSelectionEndSample > mSelectionStartSample)
                {
                    mStreamStart = mSelectionStartSample;
                    mStreamEnd = mSelectionEndSample;
                }
                else
                {
                    mStreamStart = mCursorPosSample;
                    mStreamEnd = mDrawSource.Length;
                }
                mPlaySource.Position = mStreamStart;
                mMusicPlayer.Play();
            }
        }

        //public float[] GetSelectedRegion()
        //{
        //    if (mDrawWave & SelectionMade)
        //    {
        //        int size = mSelectionEndSample - mSelectionStartSample + 1;
        //        float[] b = new float[size];
        //        mSoundSource.Position = mSelectionStartSample;
        //        mSoundSource.Read(b, 0, size);
        //        return b;
        //    }
        //    else
        //    {
        //        return null;
        //    }
        //}        

        private void ScrollView(int newOffset)
        {
            if (newOffset < 0) newOffset = 0;
            if (newOffset < hrScroll.Maximum)
            {
                if (newOffset != hrScroll.Value)
                {
                    hrScroll.Value = newOffset;
                    mDrawingStartOffset = newOffset;
                    ArrangeSelectionPositions();
                    Refresh();
                }
            }
        }

        private void hrScroll_Scroll(System.Object sender, System.Windows.Forms.ScrollEventArgs e)
        {
            ScrollView(e.NewValue);
        }

        private bool ZoomedFull()
        {
            if (mDrawWave)
            {
                return (int)(mDrawSource.Length / SamplesPerPixel) <= CWidth;
            }
            else
            {
                return true;
            }
        }

        public void SetCursorToOffset(int offset, bool center)
        {
            if (mDrawWave)
            {
                if (offset < mDrawSource.Length)
                {
                    StopPlaying();
                    int x = 0;
                    if (ZoomedFull())
                    {
                        //If all wave file is visible
                        mDrawingStartOffset = 0;
                    }
                    else
                    {
                        if (center)
                        {
                            x = Math.Min(CWidth / 2, mLastDrawnPixel);
                            //Set drawing offset half page before the required offset
                            mDrawingStartOffset = (int)Math.Max(0, offset - x * mSamplesPerPixel);
                        }
                        else
                        {
                            //Set drawing offset a little before the required offset
                            x = Math.Min(CWidth / 50, mLastDrawnPixel);
                            mDrawingStartOffset = (int)Math.Max(0, offset - x * mSamplesPerPixel);
                        }
                    }
                    hrScroll.Value = (int)(mDrawingStartOffset);
                    if (hrScroll.LargeChange >= hrScroll.Maximum)
                    {
                        mDrawingStartOffset = 0;
                    }
                    else
                    {
                        mDrawingStartOffset = hrScroll.Value;
                        //küsürat varsa doğru yere çek
                    }
                    CursorPositionSample = offset;
                    ResetSelectionOffset(offset);
                    Refresh();
                }
            }
        }

        private void cursorTimer_Tick(System.Object sender, System.EventArgs e)
        {
            if ((ParentForm.ContainsFocus && (mDrawWave && (!mDrawing && !mSelecting) && !(mMusicPlayer.PlaybackState == CSCore.SoundOut.PlaybackState.Playing))))
            {
                if (((CursorPositionX < CWidth) && (CursorPositionX >= 0)))
                {
                    ControlPaint.DrawReversibleLine(this.PointToScreen(new Point(CursorPositionX, 0)), this.PointToScreen(new Point(CursorPositionX, CHeight)), Color.Gray);
                }
            }
        }

        private void playTimer_Tick(System.Object sender, System.EventArgs e)
        {
            if (mMusicPlayer.PlaybackState == CSCore.SoundOut.PlaybackState.Playing)
            {
                lblCursorPos.Text = mPlaySource.GetPosition().ToString(@"hh\:mm\:ss\.ff");
                if (mPlaySource.Position >= mStreamEnd) StopPlaying();
                else
                {
                    int playCursorX = (int)((mPlaySource.Position - mDrawingStartOffset) / mSamplesPerPixel);
                    if (playCursorX != mLastPlayCursorX)
                    {
                        if (playCursorX < CWidth && playCursorX >= 0)
                        {
                            mLastPlayCursorX = playCursorX;
                            Refresh();
                        }
                        else
                        {
                            ScrollView((int)(mPlaySource.Position));
                        }
                    }
                }
            } else
            {
                progress.Value = 0;
            }
        
        }

        public void InvokeOnUiThreadIfRequired(Action action)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(action);
            }
            else
            {
                action.Invoke();
            }
        }

        private void btnPlay_ButtonClick(object sender, EventArgs e)
        {
            PlayRegion();
        }

        private void btnStop_ButtonClick(object sender, EventArgs e)
        {
            StopPlaying();
        }

        public MusicPlayer Player
        {
            get { return mMusicPlayer; }
        }
    }
}
