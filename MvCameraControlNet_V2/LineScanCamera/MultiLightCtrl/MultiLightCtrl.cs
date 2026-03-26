using MvCameraControl;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace MultiLightCtrl
{
    public partial class MultiLightCtrl : Form
    {

        const DeviceTLayerType devLayerType = DeviceTLayerType.MvGigEDevice | DeviceTLayerType.MvGenTLCameraLinkDevice
       | DeviceTLayerType.MvGenTLCXPDevice | DeviceTLayerType.MvGenTLXoFDevice;
        IDevice _device = null;
        List<IDeviceInfo> _devInfoList = null;
        bool _bGrabbing = false;
        Thread _hReceiveThread = null;  // 取流线程
        UInt32 _exposureNum = 0;        // 曝光个数
        IntPtr[] hWnd = new IntPtr[4];  // 图像显示窗口句柄
        List<uint>  _userInputExposureNums = new List<uint> {1, 2, 4 };  // 允许用户输入的曝光个数

        public MultiLightCtrl()
        {
            InitializeComponent();

            this.Load += new EventHandler(this.MultiLightCtrl_Load);
            btnEnumDevice.Enabled = true;
            cmbMultiLight.Enabled = false;
        }

        private void MultiLightCtrl_Load(object sender, EventArgs e)
        {
            hWnd[0] = pictureBox1.Handle;
            hWnd[1] = pictureBox2.Handle;
            hWnd[2] = pictureBox3.Handle;
            hWnd[3] = pictureBox4.Handle;
            // ch: 初始化 SDK | en: Initialize SDK
            SDKSystem.Initialize();
        }

        // ch:显示错误信息 | en:Show error message
        private void ShowErrorMsg(string csMessage, int nErrorNum)
        {
            string errorMsg;
            if (nErrorNum == 0)
            {
                errorMsg = csMessage;
            }
            else
            {
                errorMsg = csMessage + ": Error =" + String.Format("{0:X}", nErrorNum);
            }

            switch (nErrorNum)
            {
                case MvError.MV_E_HANDLE: errorMsg += " Error or invalid handle "; break;
                case MvError.MV_E_SUPPORT: errorMsg += " Not supported function "; break;
                case MvError.MV_E_BUFOVER: errorMsg += " Cache is full "; break;
                case MvError.MV_E_CALLORDER: errorMsg += " Function calling order error "; break;
                case MvError.MV_E_PARAMETER: errorMsg += " Incorrect parameter "; break;
                case MvError.MV_E_RESOURCE: errorMsg += " Applying resource failed "; break;
                case MvError.MV_E_NODATA: errorMsg += " No data "; break;
                case MvError.MV_E_PRECONDITION: errorMsg += " Precondition error, or running environment changed "; break;
                case MvError.MV_E_VERSION: errorMsg += " Version mismatches "; break;
                case MvError.MV_E_NOENOUGH_BUF: errorMsg += " Insufficient memory "; break;
                case MvError.MV_E_UNKNOW: errorMsg += " Unknown error "; break;
                case MvError.MV_E_GC_GENERIC: errorMsg += " General error "; break;
                case MvError.MV_E_GC_ACCESS: errorMsg += " Node accessing condition error "; break;
                case MvError.MV_E_ACCESS_DENIED: errorMsg += " No permission "; break;
                case MvError.MV_E_BUSY: errorMsg += " Device is busy, or network disconnected "; break;
                case MvError.MV_E_NETER: errorMsg += " Network error "; break;
            }

            MessageBox.Show(errorMsg, "PROMPT");
        }

        private void DeviceListAcq()
        {
            int result = 0;

            // ch:创建设备列表 | en:Create Device List
            cmbDeviceList.Items.Clear();
            result = DeviceEnumerator.EnumDevices(devLayerType, out _devInfoList);
            if (MvError.MV_OK != result)
            {
                ShowErrorMsg("DeviceList Acquire Failed!", result);
                return;
            }

            // ch:在窗体列表中显示设备名 | en:Display device name in the form list
            for (int i = 0; i < _devInfoList.Count; i++)
            {
                IDeviceInfo deviceInfo = _devInfoList[i];
                if (deviceInfo.UserDefinedName != "")
                {
                    cmbDeviceList.Items.Add(deviceInfo.TLayerType.ToString() + ": " + deviceInfo.UserDefinedName + " (" + deviceInfo.SerialNumber + ")");
                }
                else
                {
                    cmbDeviceList.Items.Add(deviceInfo.TLayerType.ToString() + ": " + deviceInfo.ManufacturerName + " " + deviceInfo.ModelName + " (" + deviceInfo.SerialNumber + ")");
                }
            }

            // ch:选择第一项 | en:Select the first item
            if (_devInfoList.Count > 0)
            {
                cmbDeviceList.SelectedIndex = 0;
            }
        }

        private void SetCtrlWhenOpen()
        {
            btnEnumDevice.Enabled = true;
            bnOpen.Enabled = false;

            bnClose.Enabled = true;
            bnStartGrab.Enabled = true;
            bnStopGrab.Enabled = false;
            bnContinuesMode.Enabled = true;
            bnContinuesMode.Checked = true;
            bnTriggerMode.Enabled = true;
            cbSoftTrigger.Enabled = false;
            bnTriggerExec.Enabled = false;
            cmbMultiLight.Enabled = true;
        }

        private void btnEnumDevice_Click(object sender, EventArgs e)
        {
            DeviceListAcq();

            bnOpen.Enabled = true;
        }

        private void bnOpen_Click(object sender, EventArgs e)
        {
            if (_devInfoList.Count == 0 || cmbDeviceList.SelectedIndex == -1)
            {
                ShowErrorMsg("No device, please select", 0);
                return;
            }

            int ret = MvError.MV_OK;

            try
            {
                // ch:创建设备 | en:Create device
                _device = DeviceFactory.CreateDevice(_devInfoList[cmbDeviceList.SelectedIndex]);

                // ch:打开设备 | en:Open device
                ret = _device.Open();
                if (ret != MvError.MV_OK)
                {
                    ShowErrorMsg("Device open fail!", ret);
                    return;
                }

                cmbMultiLight.Items.Clear();
                IEnumValue multiLightControl;
                ret = _device.Parameters.GetEnumValue("MultiLightControl", out multiLightControl);
                if (ret != MvError.MV_OK)
                {
                    foreach (var item in _userInputExposureNums)
                    {
                        cmbMultiLight.Items.Add(item);
                    }
                    cmbMultiLight.SelectedIndex = 0;
                    _exposureNum = uint.Parse(cmbMultiLight.Text);

                    radioButtonMultiControlFromUser.Enabled = true;
                    radioButtonMultiControlFromUser.Select();
                    radioButtonMultiControlFromDevice.Enabled = false;

                    ret = MvError.MV_OK;
                }
                else
                {
                    foreach (IEnumEntry item in multiLightControl.SupportEnumEntries)
                    {
                        cmbMultiLight.Items.Add(item.Symbolic);
                        if (item.Symbolic == multiLightControl.CurEnumEntry.Symbolic)
                        {
                            cmbMultiLight.SelectedIndex = Array.IndexOf(multiLightControl.SupportEnumEntries, item); 
                        }
                    }
                    _exposureNum = multiLightControl.CurEnumEntry.Value & 0xF;
                    radioButtonMultiControlFromUser.Enabled = false;
                    radioButtonMultiControlFromDevice.Enabled = true;
                    radioButtonMultiControlFromDevice.Select();
                }

                // ch:设置采集连续模式 | en:Set Continues Acquisition Mode
                _device.Parameters.SetEnumValue("AcquisitionMode", 2);
                _device.Parameters.SetEnumValue("TriggerMode", 0);

                // ch:控件操作 | en:Control operation
                SetCtrlWhenOpen();
            }
            catch (Exception ex)
            {
                Console.Write("Exception: " + ex.Message);
            }
            finally
            {
                // ch:打开相机失败 | en:Open device failed
                if (ret != MvError.MV_OK)
                {
                    // ch:销毁设备 | en:Destroy device
                    if (_device != null)
                    {
                        _device.Dispose();
                        _device = null;
                    }
                }
            }
        }

        private void SetCtrlWhenClose()
        {
            btnEnumDevice.Enabled = true;

            bnOpen.Enabled = true;

            bnClose.Enabled = false;
            bnStartGrab.Enabled = false;
            bnStopGrab.Enabled = false;
            bnContinuesMode.Enabled = false;
            bnTriggerMode.Enabled = false;
            cbSoftTrigger.Enabled = false;
            bnTriggerExec.Enabled = false;
            cmbMultiLight.Enabled = false;
        }

        private void bnClose_Click(object sender, EventArgs e)
        {
            // ch:取流标志位清零 | en:Reset flow flag bit
            if (_bGrabbing == true)
            {
                _bGrabbing = false;
                _hReceiveThread.Join();
            }

            // ch:关闭设备 | en:Close Device
            if (_device != null)
            {
                _device.Close();
                _device.Dispose();
                _device = null;
            }

            // ch:控件操作 | en:Control Operation
            SetCtrlWhenClose();
        }

        private void cmbMultiLight_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_device == null)
            {
                return;
            }

            // 从相机中获取曝光个数
            if (radioButtonMultiControlFromDevice.Checked)
            {
                int nRet = _device.Parameters.SetEnumValueByString("MultiLightControl", cmbMultiLight.Items[cmbMultiLight.SelectedIndex].ToString());
                if (MvError.MV_OK != nRet)
                {
                    ShowErrorMsg("Set MultiLightControl failed", nRet);
                }

                IEnumValue enumValue;
                nRet = _device.Parameters.GetEnumValue("MultiLightControl", out enumValue);
                if (MvError.MV_OK == nRet)
                {
                    _exposureNum = enumValue.CurEnumEntry.Value & 0xF;
                }
            }
            else
            {
                // 用户输入曝光个数
                _exposureNum = uint.Parse(cmbMultiLight.Text);
            }
        }

        private void bnContinuesMode_CheckedChanged(object sender, EventArgs e)
        {
            if (_device == null)
            {
                return;
            }

            if (bnContinuesMode.Checked)
            {
                _device.Parameters.SetEnumValue("TriggerMode", 0);
                cbSoftTrigger.Enabled = false;
                bnTriggerExec.Enabled = false;
            }
        }

        private void bnTriggerMode_CheckedChanged(object sender, EventArgs e)
        {
            if (_device == null)
            {
                return;
            }

             // ch:打开触发模式 | en:Open Trigger Mode
            if (bnTriggerMode.Checked)
            {
                _device.Parameters.SetEnumValue("TriggerMode", 1);
            }

             // ch:触发源选择:0 - Line0; | en:Trigger source select:0 - Line0;
                //           1 - Line1;
                //           2 - Line2;
                //           3 - Line3;
                //           4 - Counter;
                //           7 - Software;
            if (cbSoftTrigger.Checked)
            {
                _device.Parameters.SetEnumValue("TriggerSource", 7);
                if (_bGrabbing)
                {
                    bnTriggerExec.Enabled = true;
                }
            }
            else
            {
                _device.Parameters.SetEnumValue("TriggerSource", 0);
            }
            cbSoftTrigger.Enabled = true;
        }

        private void SetCtrlWhenStartGrab()
        {
            bnStartGrab.Enabled = false;
            cmbMultiLight.Enabled = false;
            bnStopGrab.Enabled = true;

            if (bnTriggerMode.Checked && cbSoftTrigger.Checked)
            {
                bnTriggerExec.Enabled = true;
            }
        }

        public void ReceiveThreadProcess()
        {
            while(_bGrabbing)
            {
                IFrameOut frame;

                //ch：获取一帧图像 | en: Get one frame
                int ret = _device.StreamGrabber.GetImageBuffer(1000, out frame);
                if (ret != MvError.MV_OK)
                {
                    if (bnTriggerMode.Checked)
                    {
                        Thread.Sleep(5);
                    }
                    continue;
                }

                // HB格式的图像需要先解码
                IFrameOut frameOut = frame;
                if (frame.Image.PixelType.ToString().Contains("HB"))
                {
                    ret = _device.ImageDecoder.HBDecode(frame, out frameOut);
                    if (MvError.MV_OK != ret)
                    {
                        Console.WriteLine("Decode image failed, {0}", ret.ToString("X"));
                        continue;
                    }
                }

                Console.WriteLine("Get one frame: Width[{0}] , Height[{1}] , FrameNum[{2}]", frameOut.Image.Width, frameOut.Image.Height, frameOut.FrameNum);

                if (_exposureNum > 1)
                {
                    List<IImage> outImages;

                    ret = _device.ImageProcess.ReconstructImage(frameOut.Image, _exposureNum, ImageReconstructionMethod.SplitByLine, out outImages);
                    if (ret != MvError.MV_OK)
                    {
                        Console.WriteLine("Reconstruct image failed, {0}", ret.ToString("X"));
                        continue;
                    }
                    for (int i = 0; i < _exposureNum && i < outImages.Count; i++)
                    {
                        _device.ImageRender.DisplayOneFrame(hWnd[i], outImages[i]);
                    }

                    //ch: 图像使用完及时释放，防止内存快速上涨导致频繁GC | en：Release image promptly to prevent rapid memory increase leading to frequent GC.
                    foreach (var image in outImages)
                    {
                        image.Dispose();
                    }
                }
                else
                {
                    _device.ImageRender.DisplayOneFrame(hWnd[0], frameOut.Image);
                }

                //ch: 图像使用完及时释放，防止内存快速上涨导致频繁GC | en：Release image promptly to prevent rapid memory increase leading to frequent GC.
                if (frameOut != frame)
                {
                    frameOut.Image.Dispose();
                }
                
                //ch: 释放图像缓存  | en: Release the image buffer
                _device.StreamGrabber.FreeImageBuffer(frame);
            }
        }

        private void bnStartGrab_Click(object sender, EventArgs e)
        {
            if (_device == null)
            {
                return;
            }

            pictureBox1.Refresh();
            pictureBox2.Refresh();
            pictureBox3.Refresh();
            pictureBox4.Refresh();
            // ch:标志位置位true | en:Set position bit true
            _bGrabbing = true;

            _hReceiveThread = new Thread(ReceiveThreadProcess);
            _hReceiveThread.Start();

            // ch:开始采集 | en:Start Grabbing
            int ret = _device.StreamGrabber.StartGrabbing();
            if (MvError.MV_OK != ret)
            {
                _bGrabbing = false;
                _hReceiveThread.Join();
                ShowErrorMsg("Start Grabbing Fail!", ret);
                return;
            }

            // ch:控件操作 | en:Control Operation
            SetCtrlWhenStartGrab();
        }

        private void SetCtrlWhenStopGrab()
        {
            bnStartGrab.Enabled = true;
            cmbMultiLight.Enabled = true;
            bnStopGrab.Enabled = false;

            bnTriggerExec.Enabled = false;
        }

        private void bnStopGrab_Click(object sender, EventArgs e)
        {
            if (_device == null)
            {
                return;
            }

            // ch:标志位设为false | en:Set flag bit false
            _bGrabbing = false;
            _hReceiveThread.Join();

            // ch:停止采集 | en:Stop Grabbing
            int ret = _device.StreamGrabber.StopGrabbing();
            if (ret != MvError.MV_OK)
            {
                ShowErrorMsg("Stop Grabbing Fail!", ret);
            }

            // ch:控件操作 | en:Control Operation
            SetCtrlWhenStopGrab();
        }

        private void cbSoftTrigger_CheckedChanged(object sender, EventArgs e)
        {
            if (_device == null)
            {
                return;
            }

            if (cbSoftTrigger.Checked)
            {
                // ch:触发源设为软触发 | en:Set trigger source as Software
                _device.Parameters.SetEnumValue("TriggerSource", 7);
                if (_bGrabbing)
                {
                    bnTriggerExec.Enabled = true;
                }
            }
            else
            {
                _device.Parameters.SetEnumValue("TriggerSource", 0);
                bnTriggerExec.Enabled = false;
            }
        }

        private void bnTriggerExec_Click(object sender, EventArgs e)
        {
            if (_device == null)
            {
                return;
            }

            // ch:触发命令 | en:Trigger command
            int nRet = _device.Parameters.SetCommandValue("TriggerSoftware");
            if (MvError.MV_OK != nRet)
            {
                ShowErrorMsg("Trigger Software Fail!", nRet);
            }
        }

        private void MultiLightCtrl_FormClosing(object sender, FormClosingEventArgs e)
        {
            bnClose_Click(sender, e);

            // ch: 反初始化SDK | en: Finalize SDK
            SDKSystem.Finalize();
        }
    }
}
