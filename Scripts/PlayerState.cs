using System.Diagnostics;
using Godot;

public class NetworkingPlayerState
{
    public int LastAckedFrameClientReceived { get; set; }
    public Vector3 Position { get; set; }
    public Vector3 Orientation { get; set; }
    public Vector3 Velocity { get; set; }
    public Vector3 Scale { get; set; }
    public bool Is2D { get; set; }

    // Server-side: last frame values for delta compression
    public Vector3 LastSentPosition { get; set; }
    public Vector3 LastSentOrientation { get; set; }
    public Vector3 LastSentVelocity { get; set; }
    public Vector3 LastSentScale { get; set; }

    public enum PlayerStateAndTransmissionBitMasks
    {
		kPosition = SharedProperties.SharedObjectValueSetMask.kPosition,
		kOrientation = SharedProperties.SharedObjectValueSetMask.kOrientation,
		kVelocity = SharedProperties.SharedObjectValueSetMask.kVelocity,
		kScale = SharedProperties.SharedObjectValueSetMask.kScale,
		kPlayerStateDead = 0x10,
		kPlayerStateSpawning = 0x20,
		kPlayerStatePlaying = 0x40,
        kPlayerTeleported = 0x80
    }

    public static readonly int MAX_UDP_PACKET_SIZE = 1400;
    public byte[] CurrentUDPPlayerPacket = new byte[MAX_UDP_PACKET_SIZE];
    public int CurrentUDPPlayerPacketSize = 0;

    // Position interpolation constants and state
    public static readonly float PLAYER_POSITION_EPSILON = 0.01f;
    public static readonly int INTERPOLATION_FRAMES = 6; // ~100ms at 60fps

    // Client-side: interpolation state
    public bool IsInterpolatingPosition { get; private set; } = false;
    private Vector3 _interpolationStartPosition;
    private Vector3 _interpolationTargetPosition;
    private int _interpolationFramesRemaining = 0;

    public int WhichPlayerAreWeOnServer  { get; set; }= 0;

    // Client-to-server input packet
    public static readonly int MAX_INPUT_PACKET_SIZE = 1024;
    private byte[] inputPacketBuffer = new byte[MAX_INPUT_PACKET_SIZE];
    public int inputSequenceNumber = 0;

    public bool IsOnServer = false;

    // Server-side: network client ID for remote players (-1 if local or not connected)
    public int NetworkClientId = -1;

    // Server-side: tracks if client has acknowledged initial game state
    public bool ReadyForGame = false;
    
    // Server-side: tracks if client has acknowledged initial game state
    public ulong InGameObjectInstanceID = 0;

    static readonly float[] FOV = {90.0f, 70.0f};

    public NetworkingPlayerState(bool is2D = false)
    {
        LastAckedFrameClientReceived = -1;
        Position = Vector3.Zero;
        Orientation = Vector3.Zero;
        Velocity = Vector3.Zero;
        Scale = Vector3.One;
        Is2D = is2D;
        // Initialize last sent values to trigger full send on first frame
        LastSentPosition = new Vector3(float.NaN, float.NaN, float.NaN);
        LastSentOrientation = new Vector3(float.NaN, float.NaN, float.NaN);
        LastSentVelocity = new Vector3(float.NaN, float.NaN, float.NaN);
        LastSentScale = new Vector3(float.NaN, float.NaN, float.NaN);
    }

    /// <summary>
    /// Called on the client when receiving player transform data from the server.
    /// Handles reconciliation between client-predicted position and server-authoritative position.
    /// </summary>
    /// <param name="stateMask">The full state/transform mask byte containing player state flags</param>
    /// <param name="newPosition">New position if kPosition flag is set</param>
    /// <param name="newOrientation">New orientation if kOrientation flag is set</param>
    /// <param name="newVelocity">New velocity if kVelocity flag is set</param>
    /// <param name="newScale">New scale if kScale flag is set</param>
    public void ReceiveTransformFromServer(byte stateMask, Vector3? newPosition, Vector3? newOrientation, Vector3? newVelocity, Vector3? newScale)
    {
        bool playerTeleported = (stateMask & (byte)PlayerStateAndTransmissionBitMasks.kPlayerTeleported) != 0;
        bool playerDead = (stateMask & (byte)PlayerStateAndTransmissionBitMasks.kPlayerStateDead) != 0;
        bool playerSpawning = (stateMask & (byte)PlayerStateAndTransmissionBitMasks.kPlayerStateSpawning) != 0;

        if (newPosition.HasValue)
        {
            if (playerTeleported || playerSpawning)
            {
                // Snap directly to position - no interpolation
                Position = newPosition.Value;
                CancelPositionInterpolation();
            }
            else
            {
                // Check if delta exceeds epsilon - if so, interpolate
                float delta = (Position - newPosition.Value).Length();
                if (delta > PLAYER_POSITION_EPSILON)
                {
                    // Start interpolation from current position to target
                    _interpolationStartPosition = Position;
                    _interpolationTargetPosition = newPosition.Value;
                    _interpolationFramesRemaining = INTERPOLATION_FRAMES;
                    IsInterpolatingPosition = true;
                }
                else
                {
                    // Delta is small enough, just set directly
                    Position = newPosition.Value;
                }
            }
        }

        if (newOrientation.HasValue)
        {
            Orientation = newOrientation.Value;
        }

        if (newVelocity.HasValue)
        {
            Velocity = newVelocity.Value;
        }

        if (newScale.HasValue)
        {
            Scale = newScale.Value;
        }
    }

    /// <summary>
    /// Advances position interpolation by one frame. Call this each frame on the client.
    /// </summary>
    public void TickPositionInterpolation()
    {
        if (!IsInterpolatingPosition)
            return;

        _interpolationFramesRemaining--;

        if (_interpolationFramesRemaining <= 0)
        {
            // Interpolation complete - snap to target
            Position = _interpolationTargetPosition;
            IsInterpolatingPosition = false;
        }
        else
        {
            // Calculate interpolation progress (0 = start, 1 = end)
            float t = 1.0f - ((float)_interpolationFramesRemaining / INTERPOLATION_FRAMES);
            Position = _interpolationStartPosition.Lerp(_interpolationTargetPosition, t);
        }
    }

    /// <summary>
    /// Cancels any in-progress position interpolation.
    /// </summary>
    public void CancelPositionInterpolation()
    {
        IsInterpolatingPosition = false;
        _interpolationFramesRemaining = 0;
    }

    public void TransmitUDPFromServerToClient()
    {
        if (IsOnServer)
        {
            // Local player - call client receive directly
            Globals.worldManager_client.networkManager_client.FramePacketReceived_Client(CurrentUDPPlayerPacket, CurrentUDPPlayerPacketSize);
        }
        else
        {
            // Remote player - send via network manager
            Globals.worldManager_server.networkManager_server.SendUdpToPlayer(WhichPlayerAreWeOnServer, CurrentUDPPlayerPacket, CurrentUDPPlayerPacketSize);
        }
    }

    /// <summary>
    /// Constructs a buffer containing the initial TCP acknowledgment to send to the server.
    /// This confirms the client received and processed the game initialization data.
    /// Returns the buffer and the number of bytes written.
    /// </summary>
    public unsafe (byte[] buffer, int size) ConstructInitiatingTcpAckPacket()
    {
        int currentOffset = 0;

        fixed (byte* bufferPtr = inputPacketBuffer)
        {
            // Write packet type (1 byte)
            bufferPtr[currentOffset] = (byte)PlayerSentPacketTypes.INITIATING_TCP_ACK;
            currentOffset++;

            // Write player index (1 byte) - which player we are on the server
            bufferPtr[currentOffset] = (byte)WhichPlayerAreWeOnServer;
            currentOffset++;
        }

        return (inputPacketBuffer, currentOffset);
    }

    /// <summary>
    /// Constructs a buffer containing player input and frame acknowledgement to send to the server.
    /// Returns the buffer and the number of bytes written.
    /// </summary>
    public unsafe (byte[] buffer, int size) ConstructPlayerInputPacket()
    {
        int currentOffset = 0;

        fixed (byte* bufferPtr = inputPacketBuffer)
        {
            // Write packet type (1 byte)
            bufferPtr[currentOffset] = (byte)PlayerSentPacketTypes.PLAYER_INPUT;
            currentOffset++;

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
