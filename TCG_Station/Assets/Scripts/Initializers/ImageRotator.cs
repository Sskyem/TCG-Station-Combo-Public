using UnityEngine;

public class ImageRotator : MonoBehaviour
{
    [Header("Ustawienia obrotu")]
    public float rotationSpeed = 180f; // Stopnie na sekundę
    public bool isRotating = true;

    void Update()
    {
        if (isRotating)
        {
            transform.Rotate(0, 0, -rotationSpeed * Time.deltaTime);
        }
    }

    public void StopRotation()
    {
        isRotating = false;
    }

    public void StartRotation()
    {
        isRotating = true;
    }
}