/******************************************************************************\
* Copyright (C) 2012-2016 Leap Motion, Inc. All rights reserved.               *
* Leap Motion proprietary and confidential. Not for distribution.              *
* Use subject to the terms of the Leap Motion SDK Agreement available at       *
* https://developer.leapmotion.com/sdk_agreement, or another agreement         *
* between Leap Motion and you, your company or other organization.             *
\******************************************************************************/

namespace LeapInternal
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading;
    using System.Runtime.InteropServices;

    using Leap;

    public class Connection
    {
        private static Dictionary<int, Connection> connectionDictionary = new Dictionary<int, Connection> ();

        static Connection ()
        {
        }

        public static Connection GetConnection (int connectionKey = 0)
        {
            if (Connection.connectionDictionary.ContainsKey (connectionKey)) {
                Connection conn;
                Connection.connectionDictionary.TryGetValue (connectionKey, out conn);
                return conn;
            } else {
                Connection newConn = new Connection (connectionKey);
                connectionDictionary.Add (connectionKey, newConn);
                return newConn;
            }
        }

        public int ConnectionKey { get; private set; }

        public DistortionDictionary DistortionCache{ get; private set; }

        public CircularObjectBuffer<Frame> Frames{ get; set; }

        private ServiceFrameFactory frameFactory = new ServiceFrameFactory ();
        private Queue<Frame> pendingFrames = new Queue<Frame> (); //Holds frames until images and tracked quad are available
        private DeviceList _devices = new DeviceList ();
        private FailedDeviceList _failedDevices;

        private CircularImageBuffer _imageCache;
        private ObjectPool<ImageData> _imageDataCache;

        private CircularObjectBuffer<TrackedQuad> _quads;
        private int _frameBufferLength = 60;
        private int _imageBufferLength = 20 * 4;
        private int _quadBufferLength = 60;
        private bool _growImageMemory = false;
        private long _pendingFrameTimeOut = 100;
        //TODO determine optimum pending timeout value

        private IntPtr _leapConnection;
        private Thread _polster;
        private bool _isRunning = false;

        //Policy and enabled features
        private UInt64 _requestedPolicies = 0;
        private UInt64 _activePolicies = 0;
        private bool _policiesAreDirty = false;
        private bool _imagesAreEnabled = false;
        private bool _rawImagesAreEnabled = false;
        private bool _trackedQuadsAreEnabled = false;

        //Config change status
        private Dictionary<uint, string> _configRequests = new Dictionary<uint, string>();

        //Connection events
        public EventHandler<LeapEventArgs> LeapInit;

        public EventHandler<ConnectionEventArgs> LeapConnection;
        public EventHandler<ConnectionLostEventArgs> LeapConnectionLost;
        public EventHandler<DeviceEventArgs> LeapDevice;
        public EventHandler<DeviceEventArgs> LeapDeviceLost;
        public EventHandler<DeviceFailureEventArgs> LeapDeviceFailure;
        public EventHandler<PolicyEventArgs> LeapPolicyChange;
        public EventHandler<FrameEventArgs> LeapFrame;
        public EventHandler<ImageEventArgs> LeapImageComplete;
        public EventHandler<TrackedQuadEventArgs> LeapTrackedQuad;
        public EventHandler<LogEventArgs> LeapLogEvent;
        public EventHandler<SetConfigResponseEventArgs> LeapConfigResponse;
        public EventHandler<ConfigChangeEventArgs> LeapConfigChange;
        public EventHandler<DistortionEventArgs> LeapDistortionChange;

        private bool _disposed = false;
        private bool _needToCheckPendingFrames = false;

        //TODO revisit dispose code
        public void Dispose ()
        { 
            Dispose (true);
            GC.SuppressFinalize (this);
        }
        
        // Protected implementation of Dispose pattern.
        protected virtual void Dispose (bool disposing)
        {
            if (_disposed)
                return; 
            
            if (disposing) {
                Stop ();
            }
            
            _disposed = true;
        }

        private Connection (int connectionKey)
        {
            ConnectionKey = connectionKey;
            _leapConnection = IntPtr.Zero;

            Frames = new CircularObjectBuffer<Frame> (_frameBufferLength);
            _quads = new CircularObjectBuffer<TrackedQuad> (_quadBufferLength);
            _imageDataCache = new ObjectPool<ImageData>(_imageBufferLength, false);
            _imageCache = new CircularImageBuffer(_imageBufferLength);
        }

        public void Start ()
        {
            if (!_isRunning) {
                if (_leapConnection == IntPtr.Zero) {
                    eLeapRS result = LeapC.CreateConnection (out _leapConnection);
                    reportAbnormalResults ("LeapC CreateConnection call was ", result);
                    result = LeapC.OpenConnection (_leapConnection);
                    reportAbnormalResults ("LeapC OpenConnection call was ", result);
                }
                _isRunning = true;
                _polster = new Thread (new ThreadStart (this.processMessages));
                _polster.IsBackground = true;
                _polster.Start ();
            }
        }

        public void Stop ()
        {
            _isRunning = false;
        }


        Int64 lastFrameId = 0;
        Int64 lastImageId = Int64.MinValue;
        //Run in Polster thread, fills in object queues
        private void processMessages ()
        {
            try {
                eLeapRS result;
                LeapInit.Dispatch<LeapEventArgs> (this, new LeapEventArgs (LeapEvent.EVENT_INIT));
                while (_isRunning) {
                    if (_leapConnection != IntPtr.Zero) {
                        LEAP_CONNECTION_MESSAGE _msg = new LEAP_CONNECTION_MESSAGE ();
                        uint timeout = 1000; //TODO determine optimal timeout value
                        result = LeapC.PollConnection (_leapConnection, timeout, ref _msg);
                        reportAbnormalResults ("LeapC PollConnection call was ", result);
                        if (result == eLeapRS.eLeapRS_Success) {
                            switch (_msg.type) {
                            case eLeapEventType.eLeapEventType_Connection:
                                LEAP_CONNECTION_EVENT connection_evt = LeapC.PtrToStruct<LEAP_CONNECTION_EVENT> (_msg.eventStructPtr);
                                handleConnection (ref connection_evt);
                                break;
                            case eLeapEventType.eLeapEventType_ConnectionLost:
                                LEAP_CONNECTION_LOST_EVENT connection_lost_evt = LeapC.PtrToStruct<LEAP_CONNECTION_LOST_EVENT> (_msg.eventStructPtr);
                                handleConnectionLost (ref connection_lost_evt);
                                break;
                            case eLeapEventType.eLeapEventType_Device:
                                LEAP_DEVICE_EVENT device_evt = LeapC.PtrToStruct<LEAP_DEVICE_EVENT> (_msg.eventStructPtr);
                                handleDevice (ref device_evt);
                                break;
                            case eLeapEventType.eLeapEventType_DeviceLost:
                                LEAP_DEVICE_EVENT device_lost_evt = LeapC.PtrToStruct<LEAP_DEVICE_EVENT> (_msg.eventStructPtr);
                                handleLostDevice (ref device_lost_evt);
                                break;
                            case eLeapEventType.eLeapEventType_DeviceFailure:
                                LEAP_DEVICE_FAILURE_EVENT device_failure_evt = LeapC.PtrToStruct<LEAP_DEVICE_FAILURE_EVENT> (_msg.eventStructPtr);
                                handleFailedDevice (ref device_failure_evt);
                                break;
                            case eLeapEventType.eLeapEventType_Tracking:
                                LEAP_TRACKING_EVENT tracking_evt = LeapC.PtrToStruct<LEAP_TRACKING_EVENT> (_msg.eventStructPtr);
//                                if((LeapC.GetNow() - tracking_evt.info.timestamp) < 60000 /*microseconds*/){ //skip frames if we are getting behind
                                    lastFrameId = tracking_evt.info.frame_id;
                                    enqueueFrame (ref tracking_evt);
                                    _needToCheckPendingFrames = true;
//                                }
                                break;
                            case eLeapEventType.eLeapEventType_Image:
                                LEAP_IMAGE_EVENT image_evt = LeapC.PtrToStruct<LEAP_IMAGE_EVENT> (_msg.eventStructPtr);
                                if(lastImageId <= lastFrameId + 8){ //Skip images if they get too far behind their frames
                                    startImage (ref image_evt);
                                } //else {
                                  //  LeapC.SetImageBuffer (ref image_evt.image, IntPtr.Zero, 0); //discard image
                                //}
                                break;
                            case eLeapEventType.eLeapEventType_ImageComplete:
                                LEAP_IMAGE_COMPLETE_EVENT image_complete_evt = LeapC.PtrToStruct<LEAP_IMAGE_COMPLETE_EVENT> (_msg.eventStructPtr);
                                lastImageId = image_complete_evt.info.frame_id;
                                completeImage (ref image_complete_evt);
                                _needToCheckPendingFrames = true;
                                break;
                            case eLeapEventType.eLeapEventType_TrackedQuad:
                                LEAP_TRACKED_QUAD_EVENT quad_evt = LeapC.PtrToStruct<LEAP_TRACKED_QUAD_EVENT> (_msg.eventStructPtr); 
                                handleQuadMessage (ref quad_evt);
                                _needToCheckPendingFrames = true;
                                break;
                            case eLeapEventType.eLeapEventType_LogEvent:
                                LEAP_LOG_EVENT log_evt = LeapC.PtrToStruct<LEAP_LOG_EVENT> (_msg.eventStructPtr);
                                reportLogMessage (ref log_evt);
                                break;
                            case eLeapEventType.eLeapEventType_PolicyChange:
                                LEAP_POLICY_EVENT policy_evt = LeapC.PtrToStruct<LEAP_POLICY_EVENT> (_msg.eventStructPtr);
                                handlePolicyChange (ref policy_evt);
                                break;
                            case eLeapEventType.eLeapEventType_ConfigChange:
                                LEAP_CONFIG_CHANGE_EVENT config_change_evt = LeapC.PtrToStruct<LEAP_CONFIG_CHANGE_EVENT> (_msg.eventStructPtr);
                                handleConfigChange (ref config_change_evt);
                                break;
                            case eLeapEventType.eLeapEventType_ConfigResponse:
                                handleConfigResponse (ref _msg);
                                break;
                            default:
                                //discard unknown message types
                                Logger.Log ("Unhandled message type " + Enum.GetName (typeof(eLeapEventType), _msg.type));
                                break;
                            } //switch on _msg.type
                        } // if valid _msg.type
                        else if (result == eLeapRS.eLeapRS_NotConnected) {
                            this.LeapConnectionLost.Dispatch<ConnectionLostEventArgs> (this, new ConnectionLostEventArgs ());
                            result = LeapC.CreateConnection (out _leapConnection);
                            reportAbnormalResults ("LeapC CreateConnection call was ", result);
                            result = LeapC.OpenConnection (_leapConnection);
                            reportAbnormalResults ("LeapC OpenConnection call was ", result);
                        }
                    } // if have connection handle
                    if(_needToCheckPendingFrames == true){
                        checkPendingFrames ();
                        _needToCheckPendingFrames = false;
                    }
                } //while running
            } catch (Exception e) {
                Logger.Log ("Exception: " + e);
            }
        }

        private void checkPendingFrames ()
        {
            if (pendingFrames.Count > 0) {
                Frame pending = pendingFrames.Peek ();
                if (isFrameReady (pending) || (pending.Timestamp < LeapC.GetNow () - _pendingFrameTimeOut)) { //is ready or too late to wait
                    pendingFrames.Dequeue ();
                    Frames.Put (pending);
                    this.LeapFrame.Dispatch<FrameEventArgs> (this, new FrameEventArgs (pending));
//                    Logger.Log("Frame: " + pending.Id + " dly: " + (LeapC.GetNow () - pending.Timestamp) + ", imgs: " + pending.Images.Count);
                    checkPendingFrames (); //check the next frame if this one was ready
                }
            }
        }

        private bool isFrameReady (Frame frame)
        {
            if ((!_imagesAreEnabled || frame.Images.Count == 2) &&
                (!_rawImagesAreEnabled || frame.Images.Count == 4) &&
                (!_trackedQuadsAreEnabled || frame.TrackedQuad.IsValid)) {
                return true;
            }

            return false;
        }

        private void enqueueFrame (ref LEAP_TRACKING_EVENT trackingMsg)
        {
            Frame newFrame = frameFactory.makeFrame (ref trackingMsg);
            if (_imagesAreEnabled) {
                _imageCache.GetImagesForFrame (newFrame.Id, newFrame.Images);
            }
            if (_trackedQuadsAreEnabled)
                newFrame.TrackedQuad = this.findTrackQuadForFrame (newFrame.Id);

            pendingFrames.Enqueue (newFrame);
        }

        private void enableIRImages ()
        {
            //Create image buffers if images turned on
            if (_imageDataCache == null) {
                _imageDataCache = new ObjectPool<ImageData> (_imageBufferLength, _growImageMemory);
                _imageCache = new CircularImageBuffer (_imageBufferLength);
            }
            if (DistortionCache == null) {
                DistortionCache = new DistortionDictionary ();
            }
            _imagesAreEnabled = true;
        }

        private void startImage (ref LEAP_IMAGE_EVENT imageMsg)
        {
            if (_imagesAreEnabled) {
                ImageData newImageData = _imageDataCache.CheckOut ();
                newImageData.poolIndex = imageMsg.image.index;
                if (newImageData.pixelBuffer == null || (ulong)newImageData.pixelBuffer.Length != imageMsg.image_size) {
                    newImageData.pixelBuffer = new byte[imageMsg.image_size];
                }

                eLeapRS result = LeapC.SetImageBuffer (ref imageMsg.image, newImageData.getPinnedHandle (), imageMsg.image_size);
                reportAbnormalResults("LeapC SetImageBuffer call was ", result);
            } else {
                //If image policies have been turned off, then discard the images
                eLeapRS discardResult = LeapC.SetImageBuffer (ref imageMsg.image, IntPtr.Zero, 0);
                reportAbnormalResults("LeapC SetImageBuffer(0,0,0) call was ", discardResult);
            }
        }

        private void completeImage (ref LEAP_IMAGE_COMPLETE_EVENT imageMsg)
        {
            LEAP_IMAGE_PROPERTIES props = LeapC.PtrToStruct<LEAP_IMAGE_PROPERTIES> (imageMsg.properties);
            ImageData pendingImageData = _imageDataCache.FindByPoolIndex (imageMsg.image.index);

            if (pendingImageData != null) {
                pendingImageData.unPinHandle (); //Done with pin for unmanaged code

//                Logger.Log("Write image");
//                System.IO.File.WriteAllBytes(("img-" + pendingImageData.type + pendingImageData.frame_id + ".raw"), pendingImageData.pixelBuffer);


                DistortionData distData;
                if (!DistortionCache.TryGetValue (imageMsg.matrix_version, out distData)) {//if fails, then create new entry
                    distData = new DistortionData ();
                    distData.version = imageMsg.matrix_version;
                    distData.width = 64; //fixed value for now
                    distData.height = 64; //fixed value for now
                    distData.data = new float[(int)(2 * distData.width * distData.height)]; //2 float values per map point
                    LEAP_DISTORTION_MATRIX matrix = LeapC.PtrToStruct<LEAP_DISTORTION_MATRIX> (imageMsg.distortionMatrix);
                    Array.Copy (matrix.matrix_data, distData.data, matrix.matrix_data.Length);
                    DistortionCache.Add ((UInt64)imageMsg.matrix_version, distData);
                }

                //Signal distortion data change if necessary
                if ((props.perspective == eLeapPerspectiveType.eLeapPerspectiveType_stereo_left) && (imageMsg.matrix_version != DistortionCache.CurrentLeftMatrix) ||
                    (props.perspective == eLeapPerspectiveType.eLeapPerspectiveType_stereo_right) && (imageMsg.matrix_version != DistortionCache.CurrentRightMatrix)) { //then the distortion matrix has changed
                    DistortionCache.DistortionChange = true;
                    this.LeapDistortionChange.Dispatch<DistortionEventArgs> (this, new DistortionEventArgs ());
                } else {
                    DistortionCache.DistortionChange = false; // clear old change flag
                }
                if (props.perspective == eLeapPerspectiveType.eLeapPerspectiveType_stereo_left) {
                    DistortionCache.CurrentLeftMatrix = imageMsg.matrix_version;
                } else {
                    DistortionCache.CurrentRightMatrix = imageMsg.matrix_version;
                }

                Image newImage = frameFactory.makeImage (ref imageMsg, pendingImageData, distData);
                _imageCache.Put (newImage);
                for (int f = 0; f < pendingFrames.Count; f++) {
                    Frame frame = pendingFrames.Dequeue ();
                    if (frame.Id == newImage.Id)
                        frame.Images.Add (newImage);
                    pendingFrames.Enqueue (frame);
                }
//                Logger.Log("Image: " + newImage.SequenceId + " dly: " + (LeapC.GetNow () - newImage.Timestamp));

                this.LeapImageComplete.Dispatch<ImageEventArgs> (this, new ImageEventArgs (newImage));
            }
        }

        private void handleQuadMessage (ref LEAP_TRACKED_QUAD_EVENT quad_evt)
        {
            TrackedQuad quad = frameFactory.makeQuad (ref quad_evt);
            _quads.Put (quad);

            //TODO rework pending frame lookup -- if frame leaves queue because of timeout, it will never get its quads or images
            for (int f = 0; f < pendingFrames.Count; f++) {
                Frame frame = pendingFrames.Dequeue ();
                if (frame.Id == quad.Id)
                    frame.TrackedQuad = quad;
                
                pendingFrames.Enqueue (frame);
            }
        }

        private void handleConnection (ref LEAP_CONNECTION_EVENT connectionMsg)
        {
            //TODO update connection on CONNECTION_EVENT
            this.LeapConnection.Dispatch<ConnectionEventArgs> (this, new ConnectionEventArgs ()); //TODO Meaningful Connection event args
        }

        private void handleConnectionLost (ref LEAP_CONNECTION_LOST_EVENT connectionMsg)
        {
            //TODO update connection on CONNECTION_LOST_EVENT
            this.LeapConnectionLost.Dispatch<ConnectionLostEventArgs> (this, new ConnectionLostEventArgs ()); //TODO Meaningful ConnectionLost event args
            this.Stop();
        }

        private void handleDevice (ref LEAP_DEVICE_EVENT deviceMsg)
        {
            IntPtr deviceHandle = deviceMsg.device.handle;
            if (deviceHandle != IntPtr.Zero) {
                IntPtr device;
                eLeapRS result = LeapC.OpenDevice(deviceMsg.device, out device);
                LEAP_DEVICE_INFO deviceInfo = new LEAP_DEVICE_INFO ();
                uint defaultLength = 14;
                deviceInfo.serial_length = defaultLength;
                deviceInfo.serial = Marshal.AllocCoTaskMem ((int)defaultLength);
                deviceInfo.size = (uint)Marshal.SizeOf (deviceInfo);
                result = LeapC.GetDeviceInfo (device, out deviceInfo);
                if (result == eLeapRS.eLeapRS_InsufficientBuffer) {
                    Marshal.FreeCoTaskMem(deviceInfo.serial);
                    deviceInfo.serial = Marshal.AllocCoTaskMem ((int)deviceInfo.serial_length);
                    deviceInfo.size = (uint)Marshal.SizeOf (deviceInfo);
                    result = LeapC.GetDeviceInfo (deviceHandle, out deviceInfo);
                }

                if (result == eLeapRS.eLeapRS_Success){
                    Device apiDevice = new Device (deviceHandle,
                                           deviceInfo.h_fov, //radians
                                           deviceInfo.v_fov, //radians
                                           deviceInfo.range / 1000, //to mm 
                                           deviceInfo.baseline / 1000, //to mm 
                                           (deviceInfo.caps == (UInt32)eLeapDeviceCaps.eLeapDeviceCaps_Embedded),
                                           (deviceInfo.status == (UInt32)eLeapDeviceStatus.eLeapDeviceStatus_Streaming),
                                           Marshal.PtrToStringAnsi (deviceInfo.serial));
                    Marshal.FreeCoTaskMem(deviceInfo.serial);
                    _devices.AddOrUpdate (apiDevice);
                    this.LeapDevice.Dispatch (this, new DeviceEventArgs (apiDevice));
                }
            }
        }

        private void handleLostDevice (ref LEAP_DEVICE_EVENT deviceMsg)
        {
            Device lost = _devices.FindDeviceByHandle (deviceMsg.device.handle);
            if (lost != null) {
                _devices.Remove (lost);
                this.LeapDeviceLost.Dispatch (this, new DeviceEventArgs (lost));
            }
        }

        private void handleFailedDevice (ref LEAP_DEVICE_FAILURE_EVENT deviceMsg)
        {
            string failureMessage;
            string failedSerialNumber = "Unavailable";
            switch(deviceMsg.status){
            case eLeapDeviceStatus.eLeapDeviceStatus_BadCalibration:
                failureMessage = "Bad Calibration. Device failed because of a bad calibration record.";
                break;
            case eLeapDeviceStatus.eLeapDeviceStatus_BadControl:
                failureMessage = "Bad Control Interface. Device failed because of a USB control interface error.";
                break;
            case eLeapDeviceStatus.eLeapDeviceStatus_BadFirmware:
                failureMessage = "Bad Firmware. Device failed because of a firmware error.";
                break;
            case eLeapDeviceStatus.eLeapDeviceStatus_BadTransport:
                failureMessage = "Bad Transport. Device failed because of a USB communication error.";
                break;
            default:
                failureMessage = "Device failed for an unknown reason";
                break;
            }
            Device failed = _devices.FindDeviceByHandle (deviceMsg.hDevice);
            if (failed != null) {
                _devices.Remove (failed);
            }

            this.LeapDeviceFailure.Dispatch<DeviceFailureEventArgs> (this, 
                new DeviceFailureEventArgs ((uint)deviceMsg.status, failureMessage, failedSerialNumber)); 
        }

        private void handleConfigChange (ref LEAP_CONFIG_CHANGE_EVENT configEvent)
        {
            Logger.Log ("Config change >>>>>>>>>>>>>>>>>>>>>");
            Logger.LogStruct (configEvent);
            string config_key = "";
            _configRequests.TryGetValue(configEvent.requestId, out config_key);
            if(config_key != null)
                _configRequests.Remove(configEvent.requestId);
            this.LeapConfigChange.Dispatch<ConfigChangeEventArgs> (this, 
                new ConfigChangeEventArgs (config_key, configEvent.status, configEvent.requestId));
        }

        private void handleConfigResponse (ref LEAP_CONNECTION_MESSAGE configMsg)
        {
            Logger.Log ("Config response >>>>>>>>>>>>>>>>>>>>>");
            LEAP_CONFIG_RESPONSE_EVENT config_response_evt = LeapC.PtrToStruct<LEAP_CONFIG_RESPONSE_EVENT> (configMsg.eventStructPtr);
            string config_key = "";
            _configRequests.TryGetValue(config_response_evt.requestId, out config_key);
            if(config_key != null)
                _configRequests.Remove(config_response_evt.requestId);

            Config.ValueType dataType;
            object value;
            uint requestId = config_response_evt.requestId;
            if(config_response_evt.value.type != eLeapValueType.eLeapValueType_String){
                
                switch(config_response_evt.value.type){
                case eLeapValueType.eLeapValueType_Boolean:
                    dataType = Config.ValueType.TYPE_BOOLEAN;
                    value = config_response_evt.value.boolValue;
                    break;
                case eLeapValueType.eLeapValueType_Int32:
                    dataType = Config.ValueType.TYPE_INT32;
                    value = config_response_evt.value.intValue;
                    break;
                case eLeapValueType.eleapValueType_Float:
                    dataType = Config.ValueType.TYPE_FLOAT;
                    value = config_response_evt.value.floatValue;
                    break;
                default:
                    dataType = Config.ValueType.TYPE_UNKNOWN;
                    value = new object();
                    break;
                }
            } else {
                LEAP_CONFIG_RESPONSE_EVENT_WITH_REF_TYPE config_ref_value =
                     LeapC.PtrToStruct<LEAP_CONFIG_RESPONSE_EVENT_WITH_REF_TYPE> (configMsg.eventStructPtr);
                dataType = Config.ValueType.TYPE_STRING;
                value = config_ref_value.value.stringValue;
            }
            SetConfigResponseEventArgs args = new SetConfigResponseEventArgs(config_key, dataType, value, requestId);
            
            this.LeapConfigResponse.Dispatch<SetConfigResponseEventArgs> (this, args);
        }

        private void reportLogMessage (ref LEAP_LOG_EVENT logMsg)
        {
            Logger.LogStruct (logMsg);
            this.LeapLogEvent.Dispatch<LogEventArgs> (this, new LogEventArgs (publicSeverity (logMsg.severity), logMsg.timestamp, logMsg.message));
        }

        private MessageSeverity publicSeverity (eLeapLogSeverity leapCSeverity)
        {
            switch (leapCSeverity) {
            case eLeapLogSeverity.eLeapLogSeverity_Unknown:
                return MessageSeverity.MESSAGE_UNKNOWN;
            case eLeapLogSeverity.eLeapLogSeverity_Information:
                return MessageSeverity.MESSAGE_INFORMATION;
            case eLeapLogSeverity.eLeapLogSeverity_Warning:
                return MessageSeverity.MESSAGE_WARNING;
            case eLeapLogSeverity.eLeapLogSeverity_Critical:
                return MessageSeverity.MESSAGE_CRITICAL;
            default:
                return MessageSeverity.MESSAGE_UNKNOWN;
            }
        }

        private void handlePolicyChange (ref LEAP_POLICY_EVENT policyMsg)
        {
            this.LeapPolicyChange.Dispatch<PolicyEventArgs> (this, new PolicyEventArgs (policyMsg.current_policy, _activePolicies));

            _activePolicies = policyMsg.current_policy;

            if (_activePolicies != _requestedPolicies) {
                // This could happen when config is turned off, or
                // the this is the policy change event from the last SetPolicy, after that, the user called SetPolicy again
                Logger.Log ("Current active policy flags: " + _activePolicies);
            }

            if ((policyMsg.current_policy & (UInt64)eLeapPolicyFlag.eLeapPolicyFlag_Images)
                == (UInt64)eLeapPolicyFlag.eLeapPolicyFlag_Images)
                enableIRImages ();

            if ((policyMsg.current_policy & (UInt64)eLeapPolicyFlag.eLeapPolicyFlag_RawImages)
                == (UInt64)eLeapPolicyFlag.eLeapPolicyFlag_RawImages)
                _rawImagesAreEnabled = true;

            //TODO Handle other (non-image) policy changes; handle policy disable
        }

        public void SetPolicy (Controller.PolicyFlag policy)
        {
            UInt64 setFlags = (ulong)flagForPolicy (policy);
            _requestedPolicies = _requestedPolicies | setFlags;
            _policiesAreDirty = true;
            setFlags = _requestedPolicies;
            UInt64 clearFlags = ~_requestedPolicies; //inverse of desired policies

            eLeapRS result = LeapC.SetPolicyFlags (_leapConnection, setFlags, clearFlags);
            reportAbnormalResults("LeapC SetPolicyFlags call was ", result);
        }

        public void ClearPolicy (Controller.PolicyFlag policy)
        {
            UInt64 clearFlags = (ulong)flagForPolicy (policy);
            _requestedPolicies = _requestedPolicies & ~clearFlags;
            _policiesAreDirty = true; //request occurs in message loop
            eLeapRS result = LeapC.SetPolicyFlags (_leapConnection, 0, clearFlags);
            reportAbnormalResults("LeapC SetPolicyFlags call was ", result);
        }

        private eLeapPolicyFlag flagForPolicy (Controller.PolicyFlag singlePolicy)
        {
            switch (singlePolicy) {
            case Controller.PolicyFlag.POLICY_BACKGROUND_FRAMES:
                return eLeapPolicyFlag.eLeapPolicyFlag_BackgroundFrames;
            case Controller.PolicyFlag.POLICY_OPTIMIZE_HMD:
                return eLeapPolicyFlag.eLeapPolicyFlag_OptimizeHMD;
            case Controller.PolicyFlag.POLICY_IMAGES:
                return eLeapPolicyFlag.eLeapPolicyFlag_Images;
            case Controller.PolicyFlag.POLICY_RAW_IMAGES:
                return eLeapPolicyFlag.eLeapPolicyFlag_RawImages;
            case Controller.PolicyFlag.POLICY_DEFAULT:
                return 0;
            default:
                return 0;
            }
        }

        /**
     * Gets the active setting for a specific policy.
     *
     * Keep in mind that setting a policy flag is asynchronous, so changes are
     * not effective immediately after calling setPolicyFlag(). In addition, a
     * policy request can be declined by the user. You should always set the
     * policy flags required by your application at startup and check that the
     * policy change request was successful after an appropriate interval.
     *
     * If the controller object is not connected to the Leap Motion software, then the default
     * state for the selected policy is returned.
     *
     * \include Controller_isPolicySet.txt
     *
     * @param flags A PolicyFlag value indicating the policy to query.
     * @returns A boolean indicating whether the specified policy has been set.
     * @since 2.1.6
     */
        public bool IsPolicySet (Controller.PolicyFlag policy)
        {
            UInt64 policyToCheck = (ulong)flagForPolicy (policy);
            return (_activePolicies & policyToCheck) == policyToCheck;
        }

        /**
     * Returns a timestamp value as close as possible to the current time.
     * Values are in microseconds, as with all the other timestamp values.
     *
     * @since 2.2.7
     *
     */
        public long Now ()
        {
            return LeapC.GetNow ();
        }

        public uint GetConfigValue(string config_key){
            uint requestId;
            eLeapRS result = LeapC.RequestConfigValue(_leapConnection, config_key, out requestId);
            reportAbnormalResults ("LeapC RequestConfigValue call was ", result);
            _configRequests.Add(requestId, config_key);
            return requestId;
        }

        public uint SetConfigValue<T>(string config_key, T value) where T : IConvertible{
            uint requestId = 0;
            eLeapRS result;
            Type dataType = value.GetType();
            if(dataType == typeof(bool)){
                result = LeapC.SaveConfigValue(_leapConnection, config_key, Convert.ToBoolean(value), out requestId);
            } else if (dataType == typeof(Int32)){
                result = LeapC.SaveConfigValue(_leapConnection, config_key, Convert.ToInt32(value), out requestId); 
            } else if (dataType == typeof(float)){
                result = LeapC.SaveConfigValue(_leapConnection, config_key, Convert.ToSingle(value), out requestId); 
            } else if(dataType == typeof(string)){
                result = LeapC.SaveConfigValue(_leapConnection, config_key, Convert.ToString(value), out requestId); 
            } else {
                throw new ArgumentException("Only boolean, Int32, float, and string types are supported.");
            }
            reportAbnormalResults ("LeapC SaveConfigValue call was ", result);

            return requestId;
        }
        public uint SetConfigValue(string config_key, bool value){
            uint requestId;
            eLeapRS result = LeapC.SaveConfigValue(_leapConnection, config_key, value, out requestId);
            reportAbnormalResults ("LeapC SaveConfigValue call was ", result);
            _configRequests.Add(requestId, config_key);

            return requestId;
        }
        public uint SetConfigValue(string config_key, Int32 value){
            uint requestId;
            eLeapRS result = LeapC.SaveConfigValue(_leapConnection, config_key, value, out requestId);
            reportAbnormalResults ("LeapC SaveConfigValue call was ", result);
            _configRequests.Add(requestId, config_key);

            return requestId;
        }
        public uint SetConfigValue(string config_key, float value){
            uint requestId;
            eLeapRS result = LeapC.SaveConfigValue(_leapConnection, config_key, value, out requestId);
            reportAbnormalResults ("LeapC SaveConfigValue call was ", result);
            _configRequests.Add(requestId, config_key);

            return requestId;
        }
        public uint SetConfigValue(string config_key, string value){
            uint requestId;
            eLeapRS result = LeapC.SaveConfigValue(_leapConnection, config_key, value, out requestId);
            reportAbnormalResults ("LeapC SaveConfigValue call was ", result);
            _configRequests.Add(requestId, config_key);

            return requestId;
        }
        /**
     * Reports whether your application has a connection to the Leap Motion
     * daemon/service. Can be true even if the Leap Motion hardware is not available.
     * @since 1.2
     */
        public bool IsServiceConnected {
            get {
                if (_leapConnection == IntPtr.Zero)
                    return false;
                
                LEAP_CONNECTION_INFO pInfo;
                eLeapRS result = LeapC.GetConnectionInfo (_leapConnection, out pInfo);
                reportAbnormalResults ("LeapC GetConnectionInfo call was ", result);

                if (pInfo.status == eLeapConnectionStatus.eLeapConnectionStatus_Connected)
                    return true;
                
                return false;
            }
        }

        /**
     * Reports whether this Controller is connected to the Leap Motion service and
     * the Leap Motion hardware is plugged in.
     *
     * When you first create a Controller object, isConnected() returns false.
     * After the controller finishes initializing and connects to the Leap Motion
     * software and if the Leap Motion hardware is plugged in, isConnected() returns true.
     *
     * You can either handle the onConnect event using a Listener instance or
     * poll the isConnected() function if you need to wait for your
     * application to be connected to the Leap Motion software before performing some other
     * operation.
     *
     * \include Controller_isConnected.txt
     * @returns True, if connected; false otherwise.
     * @since 1.0
     */
        public bool IsConnected {
            get {
                return IsServiceConnected && Devices.Count > 0;
            } 
        }

        public void GetLatestImages (ref ImageList receiver)
        {
            _imageCache.GetLatestImages (receiver);
        }

        public int GetFrameImagesForFrame (long frameId, ref ImageList images)
        {
            return _imageCache.GetImagesForFrame (frameId, images);
        }

        private TrackedQuad findTrackQuadForFrame (long frameId)
        {
            TrackedQuad quad = null;
            for (int q = 0; q < _quads.Count; q++) {
                quad = _quads.Get (q);
                if (quad.Id == frameId)
                    return quad;
                if (quad.Id < frameId)
                    break;
            }
            return quad; //null
        }

        public TrackedQuad GetLatestQuad ()
        {
            return _quads.Get (0);
        }

        /**
     * The list of currently attached and recognized Leap Motion controller devices.
     *
     * The Device objects in the list describe information such as the range and
     * tracking volume.
     *
     * \include Controller_devices.txt
     *
     * Currently, the Leap Motion Controller only allows a single active device at a time,
     * however there may be multiple devices physically attached and listed here.  Any active
     * device(s) are guaranteed to be listed first, however order is not determined beyond that.
     *
     * @returns The list of Leap Motion controllers.
     * @since 1.0
     */
        public DeviceList Devices {
            get {
                if (_devices == null) {
                    _devices = new DeviceList ();
                }

                return _devices;
            } 
        }

        public FailedDeviceList FailedDevices {
            get {
                if (_failedDevices == null) {
                    _failedDevices = new FailedDeviceList ();
                }
                
                return _failedDevices;
            } 
        }

        public bool IsPaused {
            get {
                return false; //TODO implement IsPaused
            }
        }

        public void SetPaused (bool newState)
        {
            //TODO implement pausing
        }

        private eLeapRS _lastResult;

        private void reportAbnormalResults (string context, eLeapRS result)
        {
            if (result != eLeapRS.eLeapRS_Success &&
               result != _lastResult) {
                string msg = context + " " + result;
                this.LeapLogEvent.Dispatch<LogEventArgs> (this, 
                    new LogEventArgs (MessageSeverity.MESSAGE_CRITICAL,
                        LeapC.GetNow (),
                        msg)
                );
            }
            _lastResult = result;
        }
    }
}
