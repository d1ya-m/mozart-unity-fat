using System.Collections;
using Arcor2.ClientSdk.ClientServices.Enums;
using UnityEngine;

public class TestPass : MonoBehaviour
{
    OVRPassthroughLayer passthroughLayer;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        passthroughLayer = GetComponent<OVRPassthroughLayer>();
        StartCoroutine(CallMethodWithDelay());
    }

    // Update is called once per frame
    void Update()
    {
        
    }

    IEnumerator CallMethodWithDelay()
    {
        yield return new WaitForSeconds(2f); // poèkej 2 sekundy
        DoSomething();
    }

    void DoSomething()
    {
        
    }
}
