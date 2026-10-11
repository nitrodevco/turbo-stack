# A "fresh Ubuntu server": systemd as PID 1 and almost nothing else, so install.sh does the real work.
ARG UBUNTU=24.04
FROM ubuntu:${UBUNTU}

ENV container=docker \
    DEBIAN_FRONTEND=noninteractive

# Roughly what a minimal cloud image ships with.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        systemd systemd-sysv dbus openssh-server sudo curl ca-certificates iproute2 \
    && rm -rf /var/lib/apt/lists/* \
    # Units that make no sense (or hang) in a container.
    && systemctl mask \
        systemd-logind.service getty.target console-getty.service \
        systemd-udevd.service systemd-udev-trigger.service \
        systemd-modules-load.service sys-kernel-config.mount sys-kernel-debug.mount \
        systemd-remount-fs.service systemd-firstboot.service \
    && systemctl enable ssh.service

STOPSIGNAL SIGRTMIN+3
CMD ["/sbin/init"]
