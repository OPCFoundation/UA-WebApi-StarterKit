## NodeJS WebApi Client

### Introduction

[Node.js](https://nodejs.org/en) is a popular JavaScript runtime environment that is used for Server and Client development. 

After installing node, the sample can be built with following commands:

```
cd .\measurements\
npm install

cd .\client\
npm install
```

After building, the sample can be run with following commands:

```
node .\client\app.js
```

[Visual Studio Code](https://code.visualstudio.com/) can be used to edit and debug the sample.

### OPC UA WebApi

The client uses stubs autogenerated with the [OpenAPI Generator](https://openapi-generator.tech/). The stubs for TypeScript are published in the [GitHub respository](https://github.com/OPCFoundation/opcua-webapi-typescript). The GitHub stubs have been fixed up to allow for ES6 and CommonJS exports and incorporate the NodeId and BrowseName constants. The repository is a [submodule](../opcua-webapi/typescript) and built when the sample application is built.

### Custom Information Models

The Server defines a custom Information Model described by a [NodeSet](../NodeSets/Measurements.NodeSet2.xml). 

A client that wishes to use these custom information models needs definitions for NodeIds and BrowseNames defined by the model. The [UA ModelCompiler](https://github.com/OPCFoundation/UA-ModelCompiler) takes a NodeSet as input and produces the constant declarations in different programming languages. 

The TypeScript declarations for the Measurements information model have been prebuilt [here](../Model/Constanst/TypeScript/). Instructions on building the constants can be found [here](../NodeSets). 

A client that needs to consume structure defined in a custom information model needs to generate classes from the NodeSet. The [OpenAPI Generator](https://openapi-generator.tech/) a widely used tool that normally is used to generate code for REST APIs, however, the classes can be reused for other purposes. For that reason [UA ModelCompiler](https://github.com/OPCFoundation/UA-ModelCompiler) also generates an [OpenAPI definition](../Model/Measurements/measurements.openapi.json) from the NodeSet. 

The [script](./generate_model_classes.ps1) creates a TypeScript project that the Node.js application can import to get the JavaScript classes for the DataTypes defined in the NodeSet. 
