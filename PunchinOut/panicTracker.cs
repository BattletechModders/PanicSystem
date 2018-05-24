﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BattleTech;

namespace PunchinOut
{

    public enum PanicStatus
    {
        Normal,
        Fatigued,
        Stressed,
        Panicked
    }


    public class PanicTracker
    {

        public PanicStatus pilotStatus;


        public string trackedMech;


        public bool ChangedRecently;

        public PanicTracker()
        {
            //do nothing here, if this is called, then JSON is deserializing us
        }
        public PanicTracker(Mech mech)
        {
            
     
            trackedMech = mech.GUID;
            pilotStatus = PanicStatus.Normal;
            ChangedRecently = false;
        }
    }

    public class MetaTracker
    {
        public List<PanicTracker> TrackedPilots { get; private set; }
        public DateTime SaveGameTimeStamp { get; private set; }

        public MetaTracker()
        {
            //do nothing for this is when we deserialize/serialize objects
        }

        public void SetSaveGameTime(DateTime savedate)
        {
            SaveGameTimeStamp = savedate;
        }

        public void SetTrackedPilots(List<PanicTracker> trackers)
        {
            TrackedPilots = trackers;
        }    

            
    }
}
