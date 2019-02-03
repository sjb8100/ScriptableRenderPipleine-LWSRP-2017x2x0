#ifndef __REGULARFORWARDLIGHTINGTEMPLATE_H__
#define __REGULARFORWARDLIGHTINGTEMPLATE_H__


#include "RegularForwardLightingUtils.hlsl"
#include "LightingTemplate.hlsl"


float3 ExecuteLightList(out uint numLightsProcessed, uint2 pixCoord, float3 vP, float3 vPw, float3 Vworld)
{
    uint start = 0, numLights = 0;
    GetCountAndStart(start, numLights, DIRECT_LIGHT);

    numLightsProcessed = numLights;     // mainly for debugging/heat maps
    return ExecuteLightList(start, numLights, vP, vPw, Vworld);
}


#endif
