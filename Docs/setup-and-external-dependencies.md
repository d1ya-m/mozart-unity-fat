# Setup and External Dependencies

## Overview

This application does not run as a standalone Unity client only. It depends on external backend services and additional third-party packages that must be available before the project can be built and run successfully.

## Required External Projects

### 1. ARCOR2 backend

Repository:

- https://github.com/robofit/arcor2

Purpose:

- provides the ARCOR2 session and scene data
- supplies navigation state, scene definitions, and action object metadata
- acts as the main runtime backend for the application

### 2. Mesh OBB Cutter / mesh service

Repository:

- https://github.com/Kapim/mesh-obb-cutter

Purpose:

- provides the mesh-related HTTP API used by the Unity application
- serves scene mesh bindings and downloadable mesh revisions
- supports rebuild operations from Portal Window bounding boxes
- stores and returns Alternate Scene geometry used by the portal pipeline

## Required Private Third-Party Packages

The project also depends on package sources taken from the private repository:

- https://github.com/robofit/arcor2_areditor_private/tree/main/3rdparty

Important packages mentioned for this project:

- `TriLib`
- `SimpleCollada`

These packages are required for the project setup and are not part of the public dependencies listed above.

## Access Requirement

Access to the private package source must be arranged with the Robo@FIT group:

- https://www.fit.vut.cz/research/group/robo/

If a new developer cannot obtain the `TriLib` and `SimpleCollada` package sources, the Unity project setup is incomplete.

## Unity Configuration Points

After deploying the required services, their addresses must be configured in this Unity project.

### ARCOR2 websocket endpoint

File:

- `Assets/Scripts/Managers/CommunicationManager.cs`

Current field:

- `public System.Uri ServerUri`

This is the websocket endpoint used by the Unity client to connect to the ARCOR2 backend.

### Mesh service HTTP endpoint

File:

- `Assets/Scripts/Mesh/MeshDownloadManager.cs`

Current field:

- `[SerializeField] private string meshServerBaseUrl`

This is the HTTP base URL used for mesh binding, mesh download, transform update, and rebuild requests.

## Deployment Requirement

For a functional setup, all of the following must be available:

- an ARCOR2 deployment
- a mesh-obb-cutter deployment
- access to the private `3rdparty` package source containing `TriLib` and `SimpleCollada`

Then the corresponding endpoint addresses must be filled into:

- `CommunicationManager.ServerUri`
- `MeshDownloadManager.meshServerBaseUrl`

## Important Practical Note

When testing on a real Meta Quest 3 device, it is not enough that the services are reachable from the development PC. They must be reachable from the headset on the same network path as well.

## Recommendation

If this project is handed to a new developer or deployed in another environment, validating these dependencies should be one of the very first setup steps before debugging runtime rendering or scene-loading issues.
