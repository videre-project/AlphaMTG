# AlphaMTG development image.
#
# Keep the Linux SDK in a separate stage so the image always has the SDK used
# to build the project, independent of what the MTGOSDK base image contains.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-sdk

FROM videreproject/mtgosdk:wayland AS alphamtg-base

ENV PATH="/opt/wine/bin:${PATH}"

USER root

# The :wayland image does not include the EGL/GLES libraries required by
# MTGO's WPF/ClickOnce UI under Wine.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        xauth \
        libegl1 \
        libgl1 \
        libgles2 \
        libgl1-mesa-dri \
        curl \
        unzip \
    && rm -rf /var/lib/apt/lists/*

USER wine

# AlphaMTG is a WPF application. Configure the prefix as Windows 10 and add
# the native/runtime dependencies needed by WPF and MTGO under Wine.
RUN xvfb-run -a winetricks -q --force vcrun2022 \
    && xvfb-run -a winetricks -q win10 \
    && xvfb-run -a winetricks -q dotnetdesktop8 \
    && xvfb-run -a winetricks -q dotnetdesktop10

USER root

# Use the container's known Linux SDK for builds rather than relying on the
# SDK that happens to be present in the MTGOSDK image.
COPY --from=dotnet-sdk /usr/share/dotnet /opt/dotnet
RUN ln -sf /opt/dotnet/dotnet /usr/local/bin/dotnet

USER wine

# Compose mounts a persistent Wine prefix over /home/wine/.wine. Keep the
# Windows runtime outside that mutable volume so an existing prefix cannot
# replace or downgrade the runtime used to launch AlphaMTG.
RUN cp -a '/home/wine/.wine/drive_c/Program Files/dotnet' /home/wine/windows-dotnet

USER root

# FlaUInspect's current v3 release is UIA3-only, but Wine's UIAutomation
# COM implementation cannot create its UIA3 class. The published v2 UIA2
# build works in this Wine prefix and remains suitable for inspection.
ARG FLAUINSPECT_VERSION=2.0.0
ARG FLAUINSPECT_SHA256=595c55feadf6621b1a60679c721ccf914ce7ce6a4e7e34aa636e3e964e8074c3
RUN curl --fail --location --retry 3 \
      --output /tmp/FlaUInspect.UIA2.${FLAUINSPECT_VERSION}.zip \
      "https://github.com/FlaUI/FlaUInspect/releases/download/v${FLAUINSPECT_VERSION}/FlaUInspect.UIA2.zip" \
    && echo "${FLAUINSPECT_SHA256}  /tmp/FlaUInspect.UIA2.${FLAUINSPECT_VERSION}.zip" \
      | sha256sum -c - \
    && mkdir -p /opt/FlaUInspect \
    && unzip -q /tmp/FlaUInspect.UIA2.${FLAUINSPECT_VERSION}.zip -d /opt/FlaUInspect \
    && rm /tmp/FlaUInspect.UIA2.${FLAUINSPECT_VERSION}.zip \
    && chown -R wine:wine /opt/FlaUInspect

COPY --chmod=755 docker/run-alphamtg.sh /usr/local/bin/run-alphamtg
COPY --chmod=755 docker/run-flauinspect.sh /usr/local/bin/run-flauinspect
COPY --chmod=755 docker/run-alphamtg-uia.sh /usr/local/bin/run-alphamtg-uia

USER wine
WORKDIR /workspace
