//! Target-centered RGB preparation for reviewed native object crops.
//! This is deliberately separate from the frozen preview13 ink-bounds contract.
use image::{Rgb, RgbImage, imageops::{self, FilterType}};

/// Uniformly normalize an explicit native target box, retaining available source
/// context and RGB channels. Bounds are exclusive right/bottom native pixels.
/// The virtual square has `ceil(max(target width,height)*(1+2*context))` pixels;
/// pixels outside the saved source crop are white. No ink-bounds scan occurs.
pub fn preprocess_target_rgb(
    source: &RgbImage,
    side: u32,
    target: [f64;4],
    context: f64,
)->Result<RgbImage,String> {
    if source.width()==0||source.height()==0||![64,96].contains(&side) {
        return Err("Target preprocessing requires nonempty RGB and side64 or96".into());
    }
    if !context.is_finite()||!(0.0..=1.0).contains(&context)||!target.iter().all(|x|x.is_finite()) {
        return Err("Nonfinite or invalid target/context".into());
    }
    let [x0,y0,x1,y1]=target;
    if x0<0.0||y0<0.0||x1<=x0||y1<=y0||x1>source.width() as f64||y1>source.height() as f64 {
        return Err("Target must have positive extent inside the supplied native crop".into());
    }
    let extent=((x1-x0).max(y1-y0)*(1.0+2.0*context)).ceil().max(1.0);
    if extent>16384.0{return Err("Target preprocessing virtual square exceeds16384pixel limit".into());}
    let extent=extent as u32;
    let left=((x0+x1-extent as f64)/2.0).floor() as i64;
    let top=((y0+y1-extent as f64)/2.0).floor() as i64;
    let mut square=RgbImage::from_pixel(extent,extent,Rgb([255;3]));
    // Copy only original saved RGB context. The explicit target determines the
    // framing even if CAD text, axes or colored fills occur elsewhere in crop.
    let sx0=left.max(0);let sy0=top.max(0);
    let sx1=(left+extent as i64).min(source.width() as i64);
    let sy1=(top+extent as i64).min(source.height() as i64);
    for sy in sy0..sy1 {for sx in sx0..sx1 {
        square.put_pixel((sx-left) as u32,(sy-top) as u32,*source.get_pixel(sx as u32,sy as u32));
    }}
    Ok(imageops::resize(&square,side,side,FilterType::Lanczos3))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn target_ignores_distant_annotation_and_preserves_color() {
        let mut image=RgbImage::from_pixel(200,100,Rgb([255;3]));
        for y in 40..60 {for x in 80..120 {image.put_pixel(x,y,Rgb([210,40,70]));}}
        // A distant dark CAD annotation must not recenter the crop.
        for y in 0..30 {for x in 0..30 {image.put_pixel(x,y,Rgb([0;3]));}}
        let crop=preprocess_target_rgb(&image,96,[80.,40.,120.,60.],0.15).unwrap();
        let c=crop.get_pixel(48,48);assert!(c[0]>c[1]+100&&c[2]>c[1]);
        assert!(crop.get_pixel(0,0)[0]>240);
    }
    #[test]
    fn border_target_gets_white_context_without_stretch() {
        let image=RgbImage::from_pixel(80,8,Rgb([20,70,140]));
        let crop=preprocess_target_rgb(&image,64,[0.,0.,80.,8.],0.15).unwrap();
        assert!(crop.get_pixel(32,0).0==[255;3]);
        let colored=(0..64).filter(|&y|crop.get_pixel(32,y)[0]<100).count();
        assert!((4..=7).contains(&colored));
    }
    #[test]
    fn invalid_target_is_rejected() {
        let image=RgbImage::from_pixel(8,8,Rgb([255;3]));
        assert!(preprocess_target_rgb(&image,96,[0.,0.,9.,8.],0.15).is_err());
        assert!(preprocess_target_rgb(&image,96,[0.,0.,0.,8.],0.15).is_err());
    }
}
