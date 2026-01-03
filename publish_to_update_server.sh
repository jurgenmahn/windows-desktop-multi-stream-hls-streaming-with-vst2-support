#!/bin/bash

# Validate $1 is set
if [ -z "$1" ]; then
    echo "Error: First argument (Release notes) is required" >&2
    exit 1
fi

# Validate $2 is set
if [ -z "$2" ]; then
    echo "Error: Second argument (version) is required" >&2
    exit 1
fi

# Validate $2 matches version format (x.y.z where x, y, z are digits)
if ! [[ "$2" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    echo "Error: Version must be in format x.y.z (e.g., 0.9.2)" >&2
    exit 1
fi

# Your script continues here
echo "Arguments validated: $1, version: $2"

git add . && git commit -am "$1" && git push && git tag "V$2" && git push origin tag "V$2"

scp AudioProcessorAndStreamer-Setup-$2.exe 192.168.113.2:/data/server/mahn.it/software/audioprocessorandstreamer/
 

autoupdate=$(cat << EOF
{
   "downloadUrl" : "https://www.mahn.it/software/audioprocessorandstreamer/AudioProcessorAndStreamer-Setup-$2.exe",
   "releaseNotes" : "$1",
   "version" : "$2"
}
EOF
)

echo "$autoupdate" > /tmp/autoupdate.json

scp /tmp/autoupdate.json 192.168.113.2:/data/server/mahn.it/software/audioprocessorandstreamer/
scp AudioProcessorAndStreamer-Setup-$2.exe 192.168.113.2:/data/server/mahn.it/software/audioprocessorandstreamer/