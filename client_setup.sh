#!/bin/bash
# Audio Orchestrator Client Setup Script
# Target OS: DietPi / Armbian on NanoPi NEO3
# Run as root

E_NOTROOT=87
if ! [ $(id -u) = 0 ]; then
  echo "Must be run as root."
  exit $E_NOTROOT
fi

SERVER_IP="192.168.1.10" # Default, change if needed
HOSTNAME=$(hostname)

echo ">>> Starting Client Setup for $HOSTNAME <<<"

# 1. Install Dependencies
echo "--- Installing Snapclient and Utils ---"
apt-get update
apt-get install -y snapclient alsa-utils

# 2. Configure Audio Overlay (I2S)
# This is device specific. For NanoPi NEO3 with PCM5102a, we need 'hifiberry-dac' or similar I2S overlay.
# Checking commonly used boot config locations.
echo "--- Configuring I2S Overlay ---"
if [ -f /boot/dietpi.txt ]; then
    echo "DietPi detected. Enabling I2S overlay..."
    # Note: This is a best-effort automated edit. User verification is key.
    # We are looking for the overlays line.
    if grep -q "overlays=" /boot/dietpi.txt; then
        sed -i 's/overlays=.*/overlays=hifiberry-dac/' /boot/dietpi.txt
    else
        echo "overlays=hifiberry-dac" >> /boot/dietpi.txt
    fi
    # Also need to enable soundcard in config.txt if present
    # This part is highly hardware specific and might need manual tweaking.
elif [ -f /boot/armbianEnv.txt ]; then
    echo "Armbian detected. Enabling I2S overlay..."
    if grep -q "overlays=" /boot/armbianEnv.txt; then
         sed -i 's/overlays=.*/overlays=i2s-dac/' /boot/armbianEnv.txt
    else
        echo "overlays=i2s-dac" >> /boot/armbianEnv.txt
    fi
else
    echo "WARNING: Unknown OS layout. Please manually enable I2S overlay in /boot/config.txt or equivalent."
fi

# 3. Configure Snapclient
echo "--- Configuring Snapclient Service ---"
# Defaults might be in /etc/default/snapclient
if [ -f /etc/default/snapclient ]; then
    # Backup
    cp /etc/default/snapclient /etc/default/snapclient.bak
    # Set host and HostID based on hostname for uniqueness
    echo "SNAPCLIENT_OPTS=\"--host $SERVER_IP --soundcard default --hostID $HOSTNAME\"" > /etc/default/snapclient
fi

# 4. Enable Read-Only filesystem (DietPi specific)
if command -v dietpi-drive_manager &> /dev/null; then
    echo "--- Enabling Read-Only Filesystem (DietPi) ---"
    # This typically requires interactive selection, but we are flagging it.
    echo "Please run 'dietpi-drive_manager' manually to enable Read-Only mode after verifying audio."
fi

echo ">>> Setup Complete. Reboot required. <<<"
echo "After reboot, test audio with: speaker-test -c2 -t wav"
