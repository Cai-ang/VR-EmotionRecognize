using System.Collections;
using System.Collections.Generic;
using Unity.XR.PICO.TOBSupport;
using UnityEngine;
using UnityEngine.UI;

public class CalibrationAPIs : MonoBehaviour
{

    public Slider IPDslider;
    void Start()
    {
        PXR_Enterprise.InitEnterpriseService();
        PXR_Enterprise.BindEnterpriseService((bound) =>
        {
            Debug.Log("Bind success.");
        });

        //Disable app exit confirmation popup
        PXR_Enterprise.SwitchSystemFunction(SystemFunctionSwitchEnum.SFS_BASIC_SETTING_SHOW_APP_QUIT_CONFIRM_DIALOG, SwitchEnum.S_OFF);
    }

    public void LaunchEyeTrackingCalibration()
    {
        // Force eye tracking calibration using Enterprise SDK 
        // For PICO 4 Enterprise
        PXR_Enterprise.StartActivity("com.pvr.et_calib", "com.pvr.et_calib.MainActivity", "", "", new string[0], new int[0]);

        // For PICO Neo 3 Pro (UNCOMMENT TO TEST)
        //PXR_Enterprise.StartActivity("com.tobii.usercalibration.neo3", "com.tobii.usercalibration.neo3.UnityCalibrationActivity", "", "", new string[0], new int[0]);
    }

    public void SetFixedIPD()
    {
        float value = IPDslider.value;
        // Force IPD Setting
        PXR_Enterprise.SetIPD(value, (result) => Debug.Log("IPD setting result: " + result));
    }

}
