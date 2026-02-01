using Godot;
using System.Collections.Generic;

public class FrameState
{
    public List<SharedProperties> SharedObjects { get; set; }
    public int FrameIndex { get; set; }

    public List<short> Node3DIDsDeletedForThisFrame { get; set; }

    public FrameState(int SharedPropertiesCount)
    {
        SharedObjects = new List<SharedProperties>(SharedPropertiesCount);
        for (int i = 0; i < SharedPropertiesCount; i++)
        {
            SharedObjects.Add(new SharedProperties());
        }
        Node3DIDsDeletedForThisFrame = new List<short>();
        FrameIndex = 0;
    }
}
