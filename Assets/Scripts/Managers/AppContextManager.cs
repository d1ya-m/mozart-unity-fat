using System;
using UnityEngine;

public enum AppRoom
{
    OfficeL206 = 0,
    Lab0104 = 1
}

public enum AppRuntime
{
    EditorPlay = 0,
    DeviceBuild = 1
}

[DefaultExecutionOrder(-1000)]
public class AppContextManager : Singleton<AppContextManager>
{
    private const string RoomPrefKey = "app_context.room";

    [SerializeField] private AppRoom defaultRoom = AppRoom.OfficeL206;
    [SerializeField] private bool persistRoomBetweenRuns = true;

    public AppRoom CurrentRoom { get; private set; }
    public AppRuntime CurrentRuntime { get; private set; }

    public event Action<AppRoom, AppRuntime> ContextChanged;

    private bool _initialized;

    private void Awake()
    {
        InitializeIfNeeded();
    }

    private void OnEnable()
    {
        InitializeIfNeeded();
    }

    private void InitializeIfNeeded()
    {
        if (_initialized)
        {
            return;
        }

#if UNITY_EDITOR
        CurrentRuntime = AppRuntime.EditorPlay;
#else
        CurrentRuntime = AppRuntime.DeviceBuild;
#endif

        if (persistRoomBetweenRuns && PlayerPrefs.HasKey(RoomPrefKey))
        {
            CurrentRoom = (AppRoom)PlayerPrefs.GetInt(RoomPrefKey);
        }
        else
        {
            CurrentRoom = defaultRoom;
        }

        _initialized = true;
        ContextChanged?.Invoke(CurrentRoom, CurrentRuntime);
    }

    public void SetRoom(AppRoom room)
    {
        InitializeIfNeeded();

        if (CurrentRoom == room)
        {
            return;
        }

        CurrentRoom = room;

        if (persistRoomBetweenRuns)
        {
            PlayerPrefs.SetInt(RoomPrefKey, (int)CurrentRoom);
            PlayerPrefs.Save();
        }

        ContextChanged?.Invoke(CurrentRoom, CurrentRuntime);
    }

    public void SetRoomByIndex(int roomIndex)
    {
        if (!Enum.IsDefined(typeof(AppRoom), roomIndex))
        {
            Debug.LogWarning($"Unsupported room index: {roomIndex}");
            return;
        }

        SetRoom((AppRoom)roomIndex);
    }

    public void SetOfficeL206()
    {
        SetRoom(AppRoom.OfficeL206);
    }

    public void SetLabO104()
    {
        SetRoom(AppRoom.Lab0104);
    }

    public bool IsRoom(AppRoom room)
    {
        InitializeIfNeeded();
        return CurrentRoom == room;
    }

    public bool IsRuntime(AppRuntime runtime)
    {
        InitializeIfNeeded();
        return CurrentRuntime == runtime;
    }
}
