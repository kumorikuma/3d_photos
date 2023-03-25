using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// [WIP] Doesn't do much atm
public class CameraControl : MonoBehaviour
{
    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update() {
        int xDirection = 0;
        int yDirection = 0;

        if (Input.GetKey(KeyCode.LeftArrow))
        {
            xDirection -= 1;
        }

        if (Input.GetKey(KeyCode.RightArrow)) {
            xDirection += 1;
        }

        if (Input.GetKey(KeyCode.UpArrow))
        {
            yDirection -= 1;
        }

        if (Input.GetKey(KeyCode.DownArrow)) {
            yDirection += 1;
        }


        transform.Rotate(0.0f, 0.2f * xDirection, 0.0f, Space.World);
        transform.Rotate(0.2f * yDirection, 0.0f, 0.0f, Space.Self);
    }
}
