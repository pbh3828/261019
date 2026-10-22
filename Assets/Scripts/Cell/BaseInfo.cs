using UnityEngine;

//베이스 구멍 모양과 높이 정보를 알려준다
//기준점은 베이스 바닥 가운데
public class BaseInfo : MonoBehaviour
{
    [Tooltip("이 구멍에 맞는 부품 종류")]
    public PartType holeType = PartType.Cube;

    [Tooltip("베이스 바닥에서 구멍 바닥까지 높이")]
    public float floorHeight = 0.005f;

    [Tooltip("베이스 바닥에서 윗면까지 높이")]
    public float topHeight = 0.015f;

    //베이스가 어떻게 위치하던 그자리의 구멍위치, 높이, 방향을 알려주는 변수 필요함
    //로봇이 윌드 좌표로 움직이니 이 값들도 월드 좌표로 돌려줌
    public Vector3 HoleCenter => transform.position; //구멍 중심 위치 
    public float FloorY => transform.position.y + floorHeight; //구멍 바닥 높이

    public float TopY => transform.position.y + topHeight; //베이스 윗면 높이

    public float HoleYaw => transform.eulerAngles.y; // 구멍 방향 베이스 rotation Y

    //구멍에 맞는 부품이 끼워졌는지
    public bool Accepts(PartInfo part) => part!=null &&  part.partType == holeType; 
    
}
