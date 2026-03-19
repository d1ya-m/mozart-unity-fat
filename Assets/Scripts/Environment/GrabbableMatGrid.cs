using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

public class GrabbableMatGrid : ActionObject
{
    [SerializeField]
    private GameObject matPrefab, grid;
    [SerializeField]
    private BoxCollider boxCollider;
    private int gridRows, gridCols;

    private List<List<MozartTIle>> mozartTiles = new List<List<MozartTIle>>();

    public MovePattern movePattern;

    public enum MovePattern
    {
        STILL,
        RANDOM,
        WAWE
    } 

    public override void Initialize(Arcor2.ClientSdk.ClientServices.Managers.ActionObjectManager actionObject)
    {
        foreach (var param in actionObject.Data.Meta.Parameters.AsEnumerable())
        {
          
            switch (param.Name)
            {
                case "rows":
                    if (!int.TryParse(param.Value, out gridRows))
                    {
                        gridRows = 2;
                        Debug.LogError("Failed to load rows parameter!");
                    }
                    break;
                case "cols":
                    if (!int.TryParse(param.Value, out gridCols))
                    {
                        gridRows = 2;
                        Debug.LogError("Failed to load cols parameter!");
                    }
                    break;
                case "move_pattern":
                    
                    switch (JsonConvert.DeserializeObject<string>(param.Value))
                    {
                        case "STILL":
                            movePattern = MovePattern.STILL; 
                            break;
                        case "RANDOM":
                            movePattern = MovePattern.RANDOM;
                            break;
                        case "WAWE":
                            movePattern = MovePattern.WAWE;
                            break;
                    }
                    break;
            }

        }
        

        float width = 0.15f, height = 0.15f;
        float space = 0.05f;
        float tileHeight = 0.2f;

        // krok mezi støedy dlaždic
        float stepX = width + space;
        float stepZ = height + space;

        // span mezi první a poslední støedovou pozicí (center-to-center)
        float spanX = (gridCols - 1) * stepX;
        float spanZ = (gridRows - 1) * stepZ;

        // start tak, aby støedy byly vycentrované kolem 0
        float startX = -spanX / 2f;
        float startZ = -spanZ / 2f;

        // celková velikost collideru (edge-to-edge)
        float totalWidth = gridCols * width + (gridCols - 1) * space;
        float totalDepth = gridRows * height + (gridRows - 1) * space;

        for (int i = 0; i < gridCols; i++)
        {
            mozartTiles.Add(new List<MozartTIle>());
            for (int j = 0; j < gridRows; j++)
            {
                MozartTIle tile = Instantiate(matPrefab, grid.transform).GetComponent<MozartTIle>();
                float posX = startX + i * stepX; // støed dlaždice
                float posZ = startZ + j * stepZ;

                tile.transform.localPosition = new Vector3(posX, 0f, posZ);
                mozartTiles[i].Add(tile);
                //tile.

            }
        }
        float centerLocalX = startX + spanX / 2f; // = 0 pøi centrovaném startu, ale takto je to generické
        float centerLocalZ = startZ + spanZ / 2f;
        Vector3 gridCenterWorld = grid.transform.TransformPoint(new Vector3(centerLocalX, 0f, centerLocalZ));

        // pøevedeme støed do lokálních souøadnic objektu s colliderm (gridParent)
        Vector3 colliderLocalCenter = boxCollider.transform.InverseTransformPoint(gridCenterWorld);


        boxCollider.size = new Vector3(totalWidth, tileHeight, totalDepth);
        boxCollider.center = colliderLocalCenter + new Vector3(0f, tileHeight / 2f, 0f);
        Debug.LogError(movePattern);
        if (movePattern == MovePattern.RANDOM)
        {
            Debug.LogError("moving start");
            StartCoroutine(CallEveryHalfSecond());
        }
           
    }

    IEnumerator CallEveryHalfSecond()
    {
        while (true)
        {
            foreach (var tiles in mozartTiles)
            {
                foreach (var tile in tiles)
                {
                    tile.RandomXZ(20f);
                }
            }
            yield return new WaitForSeconds(0.5f);
        }
    }

    public void InitializeGrid()
    {
        
        
    }
       
    
}
