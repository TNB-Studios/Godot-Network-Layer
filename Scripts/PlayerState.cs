using System.Diagnostics;
using Godot;

public class NetworkingPlayerState
{
    public int LastAckedFrameClientReceived { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 Orientation { get; set; }
    public Vector3 Velocity { get; set; }

    public static readonly int MAX_UDP_PACKET_SIZE = 1400;
    public byte[] CurrentUDPPlayerPacket = new byte[MAX_UDP_PACKET_SIZE];

    public int WhichPlayerAreWeOnServer  { get; set; }= 0;

    // Client-to-server input packet
    public static readonly int MAX_INPUT_PACKET_SIZE = 1024;
    private byte[] inputPacketBuffer = new byte[MAX_INPUT_PACKET_SIZE];
    private int inputSequenceNumber = 0;

    static readonly float[] FOV = {90.0f, 70.0f};

    public NetworkingPlayerState()
    {
        LastAckedFrameClientReceived = -1;
        Position = Vector3.Zero;
        Orientation = Vector3.Zero;
        Velocity = Vector3.Zero;
    }

    /// <summary>
    /// Constructs a buffer containing player input and frame acknowledgement to send to the server.
    /// Returns the buffer and the number of bytes written.
    /// </summary>
    public unsafe (byte[] buffer, int size) SendPlayerInputAndAckToServer()
    {
        int currentOffset = 0;

        fixed (byte* bufferPtr = inputPacketBuffer)
        {
            // Write player index (1 byte) - which player we are on the server
            bufferPtr[currentOffset] = (byte)WhichPlayerAreWeOnServer;
            currentOffset++;

            // Write input sequence number (4 bytes) - rolling incremental value
            *(int*)(bufferPtr + currentOffset) = inputSequenceNumber;
            currentOffset += sizeof(int);

            // Write last acked frame from server (3 bytes)
            bufferPtr[currentOffset] = (byte)(LastAckedFrameClientReceived & 0xFF);
            bufferPtr[currentOffset + 1] = (byte)((LastAckedFrameClientReceived >> 8) & 0xFF);
            bufferPtr[currentOffset + 2] = (byte)((LastAckedFrameClientReceived >> 16) & 0xFF);
            currentOffset += 3;

            // Write player position (3 floats = 12 bytes)
              *(float*)(bufferPtr + currentOffset) = Position.X;
            currentOffset += sizeof(float);
            *(float*)(bufferPtr + currentOffset) = Position.Y;
            currentOffset += sizeof(float);
            *(float*)(bufferPtr + currentOffset) = Position.Z;
            currentOffset += sizeof(float);

            // Write player orientation (3 floats = 12 bytes)
             *(float*)(bufferPtr + currentOffset) = Orientation.X;
            currentOffset += sizeof(float);
            *(float*)(bufferPtr + currentOffset) = Orientation.Y;
            currentOffset += sizeof(float);
            *(float*)(bufferPtr + currentOffset) = Orientation.Z;
            currentOffset += sizeof(float);

            // TODO: Add additional input data (buttons, movement intent, etc.)
        }

        // Increment the sequence number for the next input packet
        inputSequenceNumber++;

        return (inputPacketBuffer, currentOffset);
    }

    /// <summary>
    /// Reads player input data from a client packet on the server side.
    /// Called after player index and sequence number have been read by NetworkManager.
    /// bufferPtr should point to the start of the LastAckedFrame field.
    /// </summary>
    public unsafe void ReadSpecificPlayerInputFromClient(byte* bufferPtr, int bufferSize, int sequenceNumber)
    {
        inputSequenceNumber = sequenceNumber;
        int currentOffset = 0;

        // Read last acked frame from client (3 bytes)
        int incomingLastAckedFrame = bufferPtr[currentOffset] |
                                     (bufferPtr[currentOffset + 1] << 8) |
                                     (bufferPtr[currentOffset + 2] << 16);
        currentOffset += 3;

        // Only update LastAckedFrame if incoming value is greater - same for positioning. This might be older
        if (incomingLastAckedFrame > LastAckedFrameClientReceived)
        {
            LastAckedFrameClientReceived = incomingLastAckedFrame;
        }
        // Read player position (3 floats = 12 bytes)
        Position = new Vector3(
            *(float*)(bufferPtr + currentOffset),
            *(float*)(bufferPtr + currentOffset + sizeof(float)),
            *(float*)(bufferPtr + currentOffset + sizeof(float) * 2)
        );
        currentOffset += sizeof(float) * 3;

        // Read player orientation (3 floats = 12 bytes)
        Orientation = new Vector3(
            *(float*)(bufferPtr + currentOffset),
            *(float*)(bufferPtr + currentOffset + sizeof(float)),
            *(float*)(bufferPtr + currentOffset + sizeof(float) * 2)
        );
        currentOffset += sizeof(float) * 3;


        // TODO: Read additional input data (buttons, movement intent, etc.)
    }

    public bool DetermineSharedObjectCanBeSeenByPlayer(Vector3 objectPosition, float objectRadius, int soundIndex, float soundRadius)
    {
        bool shouldTransmit = true;
        // first, if we have a sound, we may want to force the object being sent.
        if (soundIndex != -1)
        {
            Vector3 deltaVector = objectPosition - Position;
            float deltaLength = deltaVector.Length();
            if (deltaLength < soundRadius)
            {
                shouldTransmit = true;
            }
        }
        if (!shouldTransmit)
        {
            // Check if object is within player's FOV (90° horizontal, 70° vertical)
            Vector3 toObject = objectPosition - Position;
            float distanceToObject = toObject.Length();

            if (distanceToObject > 0.001f)
            {
                Vector3 toObjectNormalized = toObject / distanceToObject;

                // Calculate player's forward direction from orientation (Y is yaw, X is pitch)
                Vector3 forward = new Vector3(
                    Mathf.Sin(Orientation.Y) * Mathf.Cos(Orientation.X),
                    -Mathf.Sin(Orientation.X),
                    Mathf.Cos(Orientation.Y) * Mathf.Cos(Orientation.X)
                ).Normalized();

                float forwardDot = toObjectNormalized.Dot(forward);

                // Early out if behind player
                if (forwardDot <= 0)
                {
                    return shouldTransmit;
                }

                Vector3 right = new Vector3(
                    Mathf.Cos(Orientation.Y),
                    0,
                    -Mathf.Sin(Orientation.Y)
                ).Normalized();

                float rightDot = toObjectNormalized.Dot(right);

                // Half FOV in radians (45° horizontal, 35° vertical)
                float halfHorizontalFOV = Mathf.DegToRad(FOV[0] * 0.5f);
                float halfVerticalFOV = Mathf.DegToRad(FOV[1] * 0.5f);

                if (objectRadius <= 1.0f)
                {
                    // Simple point-in-view test (cheaper)
                    float horizontalAngle = Mathf.Abs(Mathf.Atan2(rightDot, forwardDot));
                    float verticalAngle = Mathf.Abs(Mathf.Atan2(toObjectNormalized.Y, forwardDot));

                    if (horizontalAngle <= halfHorizontalFOV && verticalAngle <= halfVerticalFOV)
                    {
                        shouldTransmit = true;
                    }
                }
                else
                {
                    // Full sphere-in-view test
                    Vector3 up = right.Cross(forward).Normalized();
                    float upDot = toObjectNormalized.Dot(up);

                    // Calculate angular radius of the object (how much FOV it subtends)
                    float angularRadius = Mathf.Atan2(objectRadius, distanceToObject);

                    // Calculate horizontal and vertical angles to object center
                    float horizontalAngle = Mathf.Abs(Mathf.Atan2(rightDot, forwardDot));
                    float verticalAngle = Mathf.Abs(Mathf.Atan2(upDot, forwardDot));

                    // Object is visible if its edge (center - angular radius) is within FOV
                    if (horizontalAngle - angularRadius <= halfHorizontalFOV &&
                        verticalAngle - angularRadius <= halfVerticalFOV)
                    {
                        shouldTransmit = true;
                    }
                }
            }
        }
        return shouldTransmit;
    }
}
