using System;
using MonoTouch.UIKit;
using MonoTouch.Foundation;
using System.Security.Cryptography;
using System.IO;
using System.Linq;
using System.Collections.Generic;



//
//  OpenUDID.cs
//  openudid
//
//  initiated by Yann Lechelle (cofounder @Appsfire) on 8/28/11.
//  Copyright 2011 OpenUDID.org
//
//  Ported to MonoTouch by Dennis Lee (Teraport Solutions Inc)
//
//  Initiators/root branches
//      iOS code: https://github.com/ylechelle/OpenUDID
//      Android code: https://github.com/vieux/OpenUDID
//      Monotouch code : https://github.com/dj-technohead/Monotouch-OpenUDID
//
//  Contributors:
//      https://github.com/ylechelle/OpenUDID/contributors
//

/*
 http://en.wikipedia.org/wiki/Zlib_License
 
 This software is provided 'as-is', without any express or implied
 warranty. In no event will the authors be held liable for any damages
 arising from the use of this software.
 
 Permission is granted to anyone to use this software for any purpose,
 including commercial applications, and to alter it and redistribute it
 freely, subject to the following restrictions:
 
 1. The origin of this software must not be misrepresented; you must not
 claim that you wrote the original software. If you use this software
 in a product, an acknowledgment in the product documentation would be
 appreciated but is not required.
 
 2. Altered source versions must be plainly marked as such, and must not be
 misrepresented as being the original software.
 
 3. This notice may not be removed or altered from any source
 distribution.
*/

namespace Teraport.Common.IOS
{


    public class OpenUDID
    {

        static string kOpenUDIDSessionCache;
        static NSString kOpenUDIDKey = new NSString("OpenUDID");
        static NSString kOpenUDIDSlotKey = new NSString("OpenUDID_slot");
        static NSString kOpenUDIDAppUIDKey = new NSString("OpenUDID_appUID");
        static NSString kOpenUDIDTSKey = new NSString("OpenUDID_createdTS");
        static NSString kOpenUDIDOOTSKey = new NSString("OpenUDID_optOutTS");
        static NSString kOpenUDIDDomain = new NSString("org.OpenUDID");
        static NSString kOpenUDIDSlotPBPrefix = new NSString("org.OpenUDID.slot.");
        static int kOpenUDIDRedundancySlots = 100;

        public OpenUDID ()
        {
        }

        // Archive a NSDictionary inside a pasteboard of a given type
        protected void setDict(NSDictionary dict, UIPasteboard pboard)
        {
            pboard.SetData(NSKeyedArchiver.ArchivedDataWithRootObject(dict), kOpenUDIDDomain);
        }

        // Retrieve an NSDictionary from a pasteboard of a given type
        protected NSMutableDictionary getDictFromPasteboard(UIPasteboard pboard)
        {
            NSDictionary result = null;

            var item = pboard.DataForPasteboardType(kOpenUDIDDomain);
            NSObject pbItem = null;

            if (item != null)
            {
                try
                {
                    pbItem = NSKeyedUnarchiver.UnarchiveObject(item);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Unable to unarchive item {0} on pasteboard!", pboard.Name);
                }
            }

            if (result != null && result is NSDictionary)
                return NSMutableDictionary.FromDictionary(result);
            else
                return null;

        }

        // Private method to create and return a new OpenUDID
        // Theoretically, this function is called once ever per application when calling [OpenUDID value] for the first time.
        // After that, the caching/pasteboard/redundancy mechanism inside [OpenUDID value] returns a persistent and cross application OpenUDID
        //
        private string generateFreshOpenUDID()
        {
            string openUDID = string.Empty;
    
            // Next we try to use an alternative method which uses the host name, process ID, and a time stamp
            // We then hash it with md5 to get 32 bytes, and then add 4 extra random bytes
            // Collision is possible of course, but unlikely and suitable for most industry needs (e.g.. aggregate tracking)
            //

            byte[] result = null;

            string cStr = NSProcessInfo.ProcessInfo.GloballyUniqueString;

            MD5 md5 = MD5.Create();
            result = md5.ComputeHash(new MemoryStream(System.Text.Encoding.Default.GetBytes(cStr)));

            openUDID = string.Format("{0:x}{1:x}{2:x}{3:x}{4:x}{5:x}{6:x}{7:x}{8:x}{9:x}{10:x}{11:x}{12:x}{13:x}{14:x}{15:x}", result[0], result[1], result[2], result[3], 
                result[4], result[5], result[6], result[7],
                result[8], result[9], result[10], result[11],
                result[12], result[13], result[14], result[15],
                                     arc4random() % 4294967295);


        
            // Call to other developers in the Open Source community:
            //
            // feel free to suggest better or alternative "UDID" generation code above.
            // NOTE that the goal is NOT to find a better hash method, but rather, find a decentralized (i.e. not web-based)
            // 160 bits / 20 bytes random string generator with the fewest possible collisions.
            // 
        
            return openUDID;
        }

        protected int arc4random()
        {
            var rngCsp = new RNGCryptoServiceProvider();
            var randomNumber = new byte[4];
            rngCsp.GetBytes(randomNumber); //this fills randomNumber 

            return BitConverter.ToInt32(randomNumber, 0);
        }

        // Main public method that returns the OpenUDID
        // This method will generate and store the OpenUDID if it doesn't exist, typically the first time it is called
        // It will return the null udid (forty zeros) if the user has somehow opted this app out (this is subject to 3rd party implementation)
        // Otherwise, it will register the current app and return the OpenUDID
        //
    
        public string Value
        {
            get { return value(); } 
        }


        protected string value()
        {
            if (!String.IsNullOrEmpty(kOpenUDIDSessionCache))
                return kOpenUDIDSessionCache;


            var defaults = NSUserDefaults.StandardUserDefaults;

            // The AppUID will uniquely identify this app within the pastebins
            //
            NSString appUID = defaults[kOpenUDIDAppUIDKey] as NSString;
            if (appUID == null)
            {
                // generate a new uuid and store it in user defaults
      
                appUID = new NSString(Guid.NewGuid().ToString());
                defaults.SetValueForKey(appUID, kOpenUDIDAppUIDKey);
            }

            string openUDID = string.Empty;
            string myRedundancySlotPBid = string.Empty;
            NSDate optedOutDate = null;
            bool optedOut = false;
            bool saveLocalDictToDefaults = false;
            bool isCompromised = false;
            NSMutableDictionary localDict = null;

            // Do we have a local copy of the OpenUDID dictionary?
            // This local copy contains a copy of the openUDID, myRedundancySlotPBid (and unused in this block, the local bundleid, and the timestamp)
            //
            var o = defaults[kOpenUDIDKey] ;
            if (o is NSDictionary)
            {
                localDict = NSMutableDictionary.FromDictionary(o as NSDictionary);
                openUDID = localDict[kOpenUDIDKey] as NSString;
                myRedundancySlotPBid = localDict[kOpenUDIDSlotKey] as NSString;
                optedOutDate = localDict[kOpenUDIDOOTSKey] as NSDate;
                optedOut = (optedOutDate != null);
            }

    
            // Here we go through a sequence of slots, each of which being a UIPasteboard created by each participating app
            // The idea behind this is to both multiple and redundant representations of OpenUDIDs, as well as serve as placeholder for potential opt-out
            //

            string availableSlotPBid = string.Empty;
            Dictionary<string, int> frequencyDict = new Dictionary<string, int>(kOpenUDIDRedundancySlots);
            for (int n = 0; n < kOpenUDIDRedundancySlots; n++)
            {
                string slotPBid = string.Format("{0}{1}", kOpenUDIDSlotPBPrefix.ToString(), n.ToString());

                UIPasteboard slotPB = UIPasteboard.FromName(slotPBid, false);

                Console.WriteLine("SlotPB name = " + slotPBid);

                if (slotPB == null)
                {
                    // assign availableSlotPBid to be the first one available
                    if (String.IsNullOrEmpty(availableSlotPBid))
                        availableSlotPBid = slotPBid;
                }
                else
                {
                    NSDictionary dict = getDictFromPasteboard(slotPB);
                    NSString oudid = dict[kOpenUDIDKey] as NSString;

                    Console.WriteLine("SlotPB dict = " + dict);
                    if (oudid == null)
                    {
                        // availableSlotPBid could inside a non null slot where no oudid can be found
                        if (String.IsNullOrEmpty(availableSlotPBid)) 
                            availableSlotPBid = slotPBid;
                    }
                    else
                    {
                        // increment the frequency of this oudid key
                        if (frequencyDict.ContainsKey(oudid))
                            frequencyDict[oudid] = frequencyDict[oudid] + 1;
                        else
                            frequencyDict.Add(oudid, 1);
                    }

                    // if we have a match with the app unique id,
                    // then let's look if the external UIPasteboard representation marks this app as OptedOut

                    NSString gid = dict[kOpenUDIDAppUIDKey] as NSString;
                    if (gid != null && gid.ToString().Equals(appUID.ToString ()))
                    {
                        myRedundancySlotPBid = slotPBid;
                        // the local dictionary is prime on the opt-out subject, so ignore if already opted-out locally
                        if (optedOut) 
                        {
                            optedOutDate = dict[kOpenUDIDOOTSKey] as NSDate;
                            optedOut = (optedOutDate != null);   
                        }
                    }
                }
            }
    
            // sort the Frequency dict with highest occurence count of the same OpenUDID (redundancy, failsafe)
            // highest is last in the list
            //
            var sortedDict = (from entry in frequencyDict orderby entry.Value ascending select entry).ToDictionary(pair => pair.Key, pair => pair.Value);

            string mostReliableOpenUDID = string.Empty;
            if (sortedDict != null && sortedDict.Count > 0) 
            {
                mostReliableOpenUDID = sortedDict.Last().Key;
                Console.WriteLine(String.Format("Freq dict = {0}, most reliable = {1}", frequencyDict, mostReliableOpenUDID)); 
            }

        
            // if openUDID was not retrieved from the local preferences, then let's try to get it from the frequency dictionary above
            //
            if (String.IsNullOrEmpty(openUDID)) 
            {        
                if (String.IsNullOrEmpty(mostReliableOpenUDID)) 
                {
                    // this is the case where this app instance is likely to be the first one to use OpenUDID on this device
                    // we create the OpenUDID, legacy or semi-random (i.e. most certainly unique)
                    //
                    openUDID = generateFreshOpenUDID();
                } 
                else 
                {
                    // or we leverage the OpenUDID shared by other apps that have already gone through the process
                    // 
                    openUDID = mostReliableOpenUDID;
                }

                // then we create a local representation
                //
                if (localDict == null) 
                { 
                    localDict = new NSMutableDictionary();
                    localDict.SetValueForKey(new NSString(openUDID), kOpenUDIDKey);
                    localDict.SetValueForKey(new NSString(appUID), kOpenUDIDAppUIDKey);

                    if (optedOut == true)
                        localDict.SetValueForKey(NSDate.FromTimeIntervalSinceNow(0), kOpenUDIDTSKey);

                    saveLocalDictToDefaults = true;
                }
            }
            else 
            {
                // Sanity/tampering check
                //
                if (mostReliableOpenUDID != null && !mostReliableOpenUDID.Equals(openUDID))
                    isCompromised = true;
            }
    
            // Here we store in the available PB slot, if applicable
            //
            Console.WriteLine(String.Format("Available Slot {0}, existing Slot {1}", availableSlotPBid ,myRedundancySlotPBid));

            if (!String.IsNullOrEmpty(availableSlotPBid)  && (String.IsNullOrEmpty (myRedundancySlotPBid) || availableSlotPBid.Equals(myRedundancySlotPBid))) 
            {

                UIPasteboard slotPB = UIPasteboard.FromName(availableSlotPBid, true);
                slotPB.Persistent = true;
        
                // save slotPBid to the defaults, and remember to save later
                //
                if (localDict != null) 
                {
                    localDict.SetValueForKey(new NSString(availableSlotPBid), kOpenUDIDSlotKey);
                    saveLocalDictToDefaults = true;
                }
        
                // Save the local dictionary to the corresponding UIPasteboard slot
                //
                if (!String.IsNullOrEmpty (openUDID) && localDict != null)
                    setDict(localDict, slotPB);
            }

            // Save the dictionary locally if applicable
            //
            if (localDict != null && saveLocalDictToDefaults)
                defaults.SetValueForKey(localDict, kOpenUDIDKey);


            // If the UIPasteboard external representation marks this app as opted-out, then to respect privacy, we return the ZERO OpenUDID, a sequence of 40 zeros...
            // This is a *new* case that developers have to deal with. Unlikely, statistically low, but still.
            // To circumvent this and maintain good tracking (conversion ratios, etc.), developers are invited to calculate how many of their users have opted-out from the full set of users.
            // This ratio will let them extrapolate convertion ratios more accurately.
            //
            if (optedOut) 
            {
                kOpenUDIDSessionCache = Guid.Empty.ToString();
                return kOpenUDIDSessionCache;
            }
        

            // return the well earned openUDID!
            //
            if (isCompromised == true)
                throw new Exception("Found a discrepancy between stored OpenUDID (reliable) and redundant copies; one of the apps on the device is most likely corrupting the OpenUDID protocol");
   
            kOpenUDIDSessionCache = openUDID;

            return kOpenUDIDSessionCache;
        }

        public void OptOut(bool optOutValue)
        {
    
            // init call
            value();
        
            NSUserDefaults defaults = NSUserDefaults.StandardUserDefaults;
    
            // load the dictionary from local cache or create one
            var o = defaults[kOpenUDIDKey];
            NSMutableDictionary dict = null;

            if (o is NSDictionary) 
                dict = NSMutableDictionary.FromDictionary(o as NSDictionary);
            else
                dict = new NSMutableDictionary();

    
            // set the opt-out date or remove key, according to parameter
            if (optOutValue)
                dict.SetValueForKey(NSDate.FromTimeIntervalSinceNow(0), kOpenUDIDOOTSKey);
            else
                dict.Remove(kOpenUDIDOOTSKey);

            // store the dictionary locally
            defaults.SetValueForKey(dict, kOpenUDIDKey);

            Console.WriteLine("Opt out = ",dict.ContainsKey(kOpenUDIDOOTSKey).ToString());
        
            // reset memory cache 
            kOpenUDIDSessionCache = string.Empty; 
        }
    }
}


