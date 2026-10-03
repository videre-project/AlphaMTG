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

# ASP.NET Core (Kestrel) needs the Windows ASP.NET runtime next to the
# desktop runtime. Winetricks does not provide it; lay it down the way the
# Tracker image does.
ARG DOTNET_RUNTIME_VERSION=10.0.12
RUN curl -fsSL --output /tmp/aspnetcore-runtime.zip \
      "https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/${DOTNET_RUNTIME_VERSION}/aspnetcore-runtime-${DOTNET_RUNTIME_VERSION}-win-x64.zip" \
    && unzip -qo /tmp/aspnetcore-runtime.zip -d /home/wine/windows-dotnet \
    && rm /tmp/aspnetcore-runtime.zip

USER root

COPY --chmod=755 docker/run-alphamtg.sh /usr/local/bin/run-alphamtg

USER wine
WORKDIR /workspace
