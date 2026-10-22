using UnityEngine;

//부품종류
public enum PartType { Cube, Prism}

//조립에 필요한 부품 정보를 한곳에 둠
public class PartInfo : MonoBehaviour
{
    public PartType partType = PartType.Cube;

    [Tooltip("부품 중심에서 바닥까지 좊이(m) 큐브 0.015, 삼각기둥 0.013")]
    public float halfHeight = 0.015f;

    //부품 프리팹의 기준점 = 한 가운데 중심이기에
    //부품을 어떤 면위에 올려놓으려면 면높이 + 절반 높이에 둬야함 . 그냥 면 높이에 두면 부품이 반쯤 파묻힘
    //큐브는 높이가 0.03이라 절반이면  0.015

    [Tooltip("몇 도마다 같은 모양인지 큐브 90 삼각기둥 180")]
    public float symmetryDeg = 90f;

    [Tooltip("잡는 위치: 부품 중심에서 위로 몇 m 큐브 0.013 삼각기둥 0.006\n"+
        "높게 잡아야 손가락 끝이 베이스에 닿지 않고 부품 바닥을 구멍에 넣을수있다")]
    public float gripOffset = 0.013f;
}
