using Godot;
using System.Collections.Generic;

public class FrameState
{
    public List<SharedProperties> SharedObjects { get; set; }
    public int FrameIndex { get; set; }

    private List<ulong> Node3DIDsDeletedForThisFrame;

    public FrameState(int SharedPropertiesCount)
    {
        SharedObjects = new List<SharedProperties>(SharedPropertiesCount);
        FrameIndex = 0;
    }
}
